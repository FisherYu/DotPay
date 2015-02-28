﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using DFramework;
using MySql.Data.MySqlClient;
using RabbitMQ.Client;
using RabbitMQ.Client.Content;

namespace Dotpay.TaobaoMonitor
{
    /// <summary>
    /// ripple消息的消费者，等待ripple presubmit发来的tx信息(包含重试时发送的)、 submit success tx成功信息、失败信息
    /// </summary>
    internal class RippleMessageListener
    {
        private static bool started;
        private static readonly string MysqlConnectionString = ConfigurationManagerWrapper.GetDBConnectionString("taobaodb");
        private static readonly string RabbitMqConnectionString = ConfigurationManagerWrapper.GetDBConnectionString("messageQueueServerConnectString");
        private static IModel _channel;
        private const string RippleResultExchangeName = "__RippleResult_Exchange";
        private const string RippleResultAutoDepositQueue = "__RippleResult_AutoDeposit";
        private static MySqlConnection OpenConnection()
        {
            MySqlConnection connection = new MySqlConnection(MysqlConnectionString);
            connection.Open();
            return connection;
        }

        public static void Start()
        {
            if (started) return;

            InitMessageExchangeAndQueueAndRegisterConsumer();
            Log.Info("-->ripple处理消息监听器启动成功...");
            started = true;
        }

        #region private methods
        //记录txid 和lastLedgerSequence 以及first_submit_at
        private static int UpdateOrderWhenPresubmit(string txid, long lastLedgerSequence)
        {
            const string sql =
              "UPDATE taobao SET ripple_status=@ripple_status_submited,tx_lastLedgerSequence=@lastLedgerSequence," +
              "                  first_submit_at=(CASE first_submit_at WHEN first_submit_at<>NULL THEN first_submit_at ELSE @first_submit_at END)" +
              " WHERE txid=@txid AND taobao_status=@taobao_status AND (ripple_status=@ripple_status_pending OR ripple_status=@ripple_status_submited)";
            try
            {
                using (var conn = OpenConnection())
                {
                    return conn.Execute(sql, new
                    {
                        ripple_status_submited = RippleTransactionStatus.Submited,
                        first_submit_at = DateTime.Now,
                        lastLedgerSequence = lastLedgerSequence,
                        txid = txid,
                        taobao_status = "WAIT_SELLER_SEND_GOODS",
                        ripple_status_pending = RippleTransactionStatus.Pending
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error("MarkTxSuccess Exception", ex);
                return 0;
            }
        }

        //标记为成功状态--最终状态
        private static int MarkTxSuccess(string txid)
        {
            const string sql =
              "UPDATE taobao SET ripple_status=@ripple_status_new " +
              " WHERE txid=@txid AND taobao_status=@taobao_status AND ripple_status=@ripple_status_old";
            try
            {
                using (var conn = OpenConnection())
                {
                    return conn.Execute(sql, new { txid = txid, ripple_status_new = RippleTransactionStatus.Successed, taobao_status = "WAIT_SELLER_SEND_GOODS", ripple_status_old = RippleTransactionStatus.Submited });
                }
            }
            catch (Exception ex)
            {
                Log.Error("MarkTxSuccess Exception", ex);
                return 0;
            }
        }

        //标记为失败状态--最终状态
        private static int MarkTxFail(string txid, string reason)
        {
            const string sql =
              "UPDATE taobao SET ripple_status=@ripple_status_new,memo=@memo" +
              " WHERE tid=@tid AND taobao_status=@taobao_status AND ripple_status=@ripple_status_old";
            try
            {
                using (var conn = OpenConnection())
                {
                    return conn.Execute(sql, new { memo = reason, txid = txid, ripple_status_new = RippleTransactionStatus.Init, taobao_status = "WAIT_SELLER_SEND_GOODS", ripple_status_old = RippleTransactionStatus.Submited });
                }
            }
            catch (Exception ex)
            {
                Log.Error("MarkTxSuccess Exception", ex);
                return 0;
            }
        }
        #endregion

        private static IModel InitMessageExchangeAndQueueAndRegisterConsumer()
        {
            if (_channel == null)
            {
                var factory = new ConnectionFactory { Uri = RabbitMqConnectionString, AutomaticRecoveryEnabled = true };
                var connection = factory.CreateConnection();
                _channel = connection.CreateModel();
                _channel.ExchangeDeclare(RippleResultExchangeName, ExchangeType.Direct, true, false, null);
                _channel.QueueDeclare(RippleResultAutoDepositQueue, true, false, false, null);
                _channel.QueueBind(RippleResultAutoDepositQueue, RippleResultExchangeName, "");
            }
            return _channel;
        }

        #region ripple message
        [Serializable]
        internal class PresubmitMessage
        {
            public PresubmitMessage(string txId, int lastLedgerSequence)
            {
                this.TxId = txId;
                this.LastLedgerSequence = lastLedgerSequence;
            }

            public string TxId { get; set; }
            public int LastLedgerSequence { get; set; }
        }

        [Serializable]
        internal class RippleTxFinalResultMessage
        {
            public RippleTxFinalResultMessage(string txId, bool success, string reason)
            {
                this.TxId = txId;
                this.Success = success;
                this.Reason = reason;
            }

            public string TxId { get; set; }
            public bool Success { get; set; }
            public string Reason { get; set; }
        }
        #endregion

        #region consumer
        public class RippleTxMessageConsumer : DefaultBasicConsumer
        {
            //private Action<RippleTxMessage> action;
            //private Action<Exception> errorCallback;
            public RippleTxMessageConsumer(IModel model)
                : base(model) { }
            public override void HandleBasicDeliver(string consumerTag, ulong deliveryTag, bool redelivered, string exchange, string routingKey, IBasicProperties properties, byte[] body)
            {
                try
                {
                    var messageBody = Encoding.UTF8.GetString(body);
                    var presubmitMessage = IoC.Resolve<IJsonSerializer>().Deserialize<PresubmitMessage>(messageBody);
                    bool success;
                    if (presubmitMessage.LastLedgerSequence > 1)
                    {
                        //如果是presubmit消息，则更新db记录submit的txid和LastLedgerSequence
                        Log.Info("收到 presubmit 消息:" + messageBody);
                        UpdateOrderWhenPresubmit(presubmitMessage.TxId, presubmitMessage.LastLedgerSequence);
                    }
                    else
                    {
                        Log.Info("收到 tx result 消息:" + messageBody);
                        //如果是tx result 消息,则更新db记录的成功或失败状态
                        var txResultMessage = IoC.Resolve<IJsonSerializer>().Deserialize<RippleTxFinalResultMessage>(messageBody);

                        if (txResultMessage.Success)
                        {
                            success = MarkTxSuccess(txResultMessage.TxId) == 1;
                            Log.Info("tx [" + txResultMessage.TxId + "] 成功了，更新DB 成功=" + success);
                        }
                        else
                        {
                            success = MarkTxFail(txResultMessage.TxId, txResultMessage.Reason) == 1;

                            Log.Info("tx [" + txResultMessage.TxId + "] 失败了，更新DB 成功=" + success);
                        }

                    }

                    Model.BasicAck(deliveryTag, false);
                }
                catch (Exception ex)
                {
                    Log.Error("process ripple tx message error", ex);
                    Model.BasicNack(deliveryTag, false, true);
                }
            }
        }
        #endregion
    }
}
