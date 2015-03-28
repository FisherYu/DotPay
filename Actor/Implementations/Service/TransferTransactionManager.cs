using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DFramework;
using Dotpay.Actor.Implementations;
using Dotpay.Actor.Interfaces;
using Dotpay.Actor.Service.Interfaces;
using Dotpay.Actor.Tools.Interfaces;
using Dotpay.Common;
using Dotpay.Common.Enum;
using Newtonsoft.Json;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using RabbitMQ.Client;

namespace Dotpay.Actor.Service.Implementations
{
    [StatelessWorker]
    public class TransferTransactionManager : Grain, ITransferTransactionManager, IRemindable
    {
        private const string MqTransferExchangeName = Constants.TransferTransactionManagerMQName + Constants.ExechangeSuffix;
        private const string MqTransferQueueName = Constants.TransferTransactionManagerMQName + Constants.QueueSuffix;
        private const string MqTransferRouteKey = Constants.TransferTransactionManagerRouteKey;
        private const string MqToRippleQueueRouteKey = Constants.TransferToRippleRouteKey;
        private const string MqToRippleQueueName = Constants.TransferToRippleQueueName + Constants.QueueSuffix;
        private const string MqRefundExchangeName = Constants.RefundTransactionManagerMQName + Constants.ExechangeSuffix;

        private static readonly ConcurrentDictionary<Guid, TaskScheduler> OrleansSchedulerContainer =
            new ConcurrentDictionary<Guid, TaskScheduler>();

        async Task<ErrorCode> ITransferTransactionManager.SubmitTransferTransaction(
            TransferTransactionInfo transactionInfo)
        {
            var transferFromDotpay = transactionInfo.Source as TransferFromDotpayInfo;

            if (transferFromDotpay != null)
            {
                var transferTransactionId = Guid.NewGuid();
                var transferTransaction = GrainFactory.GetGrain<ITransferTransaction>(transferTransactionId);
                var transferTransactionSeq =
                    await this.GeneratorTransferTransactionSequenceNo(transactionInfo.Target.Payway);
                await transferTransaction.Initialize(transferTransactionSeq, transactionInfo);
                var message = new SubmitTransferTransactionMessage(transferTransactionId, transferFromDotpay,
                    transactionInfo.Target,
                    transactionInfo.Currency, transactionInfo.Amount, transactionInfo.Memo);
                await
                    MessageProducterManager.GetProducter()
                        .PublishMessage(message, MqTransferExchangeName, MqTransferRouteKey, true);
                return await ProcessSubmitedTransferTransaction(message);
            }

            return ErrorCode.None;
        }

        Task<ErrorCode> ITransferTransactionManager.MarkAsProcessing(Guid transferTransactionId, Guid operatorId)
        {
            var transferTransaction = GrainFactory.GetGrain<ITransferTransaction>(transferTransactionId);
            return transferTransaction.MarkAsProcessing(operatorId);
        }

        async Task<ErrorCode> ITransferTransactionManager.ConfirmTransactionFail(Guid transferTransactionId,
            Guid operatorId, string reason)
        {
            var transferTransaction = GrainFactory.GetGrain<ITransferTransaction>(transferTransactionId);
            var code = await transferTransaction.ConfirmFail(operatorId, reason);
            if (code == ErrorCode.None)
                await PublishRefundMessage(transferTransactionId);

            return code;
        }

        async Task ITransferTransactionManager.Receive(MqMessage message)
        {
            var transactionMessage = message as SubmitTransferTransactionMessage;

            if (transactionMessage != null)
            {
                await ProcessSubmitedTransferTransaction(transactionMessage);
                return;
            }

            var rippleTransactionPresubmitMessage = message as RippleTransactionPresubmitMessage;

            if (rippleTransactionPresubmitMessage != null)
            {
                await ProcessRippleTransactionPresubmitMessage(rippleTransactionPresubmitMessage);
                return;
            }
        }

        #region override
        public async Task ReceiveReminder(string reminderName, TickStatus status)
        {
            //Reminder用来做3分钟后检查RippleTx结果
            var transferTxId = new Guid(reminderName);
            var transferTx = GrainFactory.GetGrain<ITransferTransaction>(transferTxId);
            var rippleTxStatus = await transferTx.GetRippleTransactionStatus();

            if (rippleTxStatus == RippleTransactionStatus.Submited)
            {
                var rippleTxInfo = await transferTx.GetRippleTransactionInfo();
                var rippleRpcClient = GrainFactory.GetGrain<IRippleRpcClient>(0);
                var currentLedgerIndex = await rippleRpcClient.GetLastLedgerIndex();
                if (rippleTxInfo.LastLedgerIndex < currentLedgerIndex)
                {
                    var validateResult = await rippleRpcClient.ValidateRippleTx(rippleTxInfo.RippleTxId);

                    if (validateResult == RippleTransactionValidateResult.NotFound)
                    {
                        //resubmit
                        await transferTx.ReSubmitToRipple();
                        var transaferTxInfo = await transferTx.GetTransactionInfo();
                        await
                            PublishSubmitToRippleMessage(transferTxId,
                                (TransferToRippleTargetInfo)transaferTxInfo.Target,
                                transaferTxInfo.Currency, transaferTxInfo.Amount);
                    }
                    else if (validateResult == RippleTransactionValidateResult.Success)
                    {
                        //complete
                        await transferTx.RippleTransactionComplete(rippleTxInfo.RippleTxId);
                    }
                }
            }
        }


        public override async Task OnActivateAsync()
        {
            await RegisterAndBindMqQueue();
            await base.OnActivateAsync();
        } 
        #endregion

        #region Private Methods

        private Task<string> GeneratorTransferTransactionSequenceNo(Payway payway)
        {
            //sequenceNoGeneratorId设计中只有5位,SequenceNoType占据3位 payway占据2位
            var sequenceNoGeneratorId = Convert.ToInt64(SequenceNoType.TransferTransaction + payway.ToString());
            var sequenceNoGenerator = GrainFactory.GetGrain<ISequenceNoGenerator>(sequenceNoGeneratorId);

            return sequenceNoGenerator.GetNext();
        }

        private async Task<ErrorCode> ProcessSubmitedTransferTransaction(SubmitTransferTransactionMessage message)
        {
            var transferTransaction = GrainFactory.GetGrain<ITransferTransaction>(message.TransferTransactionId);
            var account = GrainFactory.GetGrain<IAccount>(message.Source.AccountId);



            if (await transferTransaction.GetStatus() == TransferTransactionStatus.Submited)
            {
                var target = message.Target as TransferToDotpayTargetInfo;

                if (target != null && !await account.Validate())
                {
                    await transferTransaction.Cancel(TransferTransactionCancelReason.TargetAccountNotExist);
                    return ErrorCode.None;
                }

                var opCode = await account.AddTransactionPreparation(message.TransferTransactionId,
                    TransactionType.TransferTransaction, PreparationType.DebitPreparation, message.Currency, message.Amount);

                //如果金额不足
                if (opCode == ErrorCode.AccountBalanceNotEnough)
                {
                    await transferTransaction.Cancel(TransferTransactionCancelReason.BalanceNotEnough);
                    await account.CancelTransactionPreparation(message.TransferTransactionId);
                }

                await transferTransaction.ConfirmTransactionPreparation();
            }

            if (await transferTransaction.GetStatus() == TransferTransactionStatus.PreparationCompleted)
            {
                await account.CommitTransactionPreparation(message.TransferTransactionId);

                if (message.Target.Payway == Payway.Ripple)
                {
                    await transferTransaction.SubmitToRipple();
                    await PublishSubmitToRippleMessage(message.TransferTransactionId,
                        (TransferToRippleTargetInfo)message.Target, message.Currency, message.Amount);
                }
            }

            return ErrorCode.None;
        }

        //处理ripple presubmit message
        private async Task ProcessRippleTransactionPresubmitMessage(RippleTransactionPresubmitMessage message)
        {
            var transferTransactionId = message.TransferTransactionId;
            var transferTransaction = GrainFactory.GetGrain<ITransferTransaction>(transferTransactionId);
            var txInfo = await transferTransaction.GetTransactionInfo();

            if (message.Currency != txInfo.Currency || txInfo.Amount != message.Amount)
            {
                var errorMsg =
                    "ProcessRippleTransactionPresubmitMessage Currency or Amount not match! tx currency={0},message currency={1}; tx amount={2}, message amount={3}"
                        .FormatWith(txInfo.Currency, message.Currency, txInfo.Amount, message.Amount);
                this.GetLogger().Error(-1, errorMsg);

                Debug.Assert((message.Currency == txInfo.Currency && txInfo.Amount != message.Amount), errorMsg);
            }

            await transferTransaction.RippleTransactionPersubmit(message.RippleTxId, message.LastLedgerIndex);
            //如果遇到一个tx，多次presubmit,每次调用presubmit都会取消上次的Reminder,最后一次的Presubmit Reminder才会生效
            await
                this.RegisterOrUpdateReminder(message.TransferTransactionId.ToString(), TimeSpan.FromMinutes(3),
                    TimeSpan.FromMinutes(2));
        }

        //处理ripple presubmit message
        private async Task ProcessRippleTransactionResultMessage(RippleTransactionResultMessage message)
        {
            var transferTransactionId = message.TransferTransactionId;
            var transferTransaction = GrainFactory.GetGrain<ITransferTransaction>(transferTransactionId);
            var txInfo = await transferTransaction.GetTransactionInfo();

            if (message.Success)
                await transferTransaction.RippleTransactionComplete(message.RippleTxId);
            else
            {
                await transferTransaction.RippleTransactionFail(message.RippleTxId, message.FailedReason);
                await PublishRefundMessage(transferTransactionId);
            }
        }

        private async Task RegisterAndBindMqQueue()
        {
            await MessageProducterManager.RegisterAndBindQueue(MqTransferExchangeName, ExchangeType.Direct, MqTransferQueueName,
                  MqTransferRouteKey, true);
            await MessageProducterManager.RegisterAndBindQueue(MqTransferExchangeName, ExchangeType.Direct, MqToRippleQueueName,
                  MqToRippleQueueRouteKey, true);
        }

        private async Task PublishSubmitToRippleMessage(Guid transferTransactionId, TransferToRippleTargetInfo target,
            CurrencyType currency, decimal amount)
        {
            var toRippleMessage = new SubmitTransferTransactionToRippleMessage(transferTransactionId,
                target, currency, amount);
            await MessageProducterManager.GetProducter()
                .PublishMessage(toRippleMessage, MqTransferExchangeName, MqToRippleQueueRouteKey, true);
        }

        private async Task PublishRefundMessage(Guid transferTransactionId)
        {
            var toRippleMessage = new RefundTransactionMessage(Guid.NewGuid(), transferTransactionId,
                RefundTransactionType.TransferRefund);
            await MessageProducterManager.GetProducter()
                .PublishMessage(toRippleMessage, MqRefundExchangeName, "", true);
        }

        #endregion
    }
}