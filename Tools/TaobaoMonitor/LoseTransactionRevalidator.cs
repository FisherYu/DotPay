﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using DFramework;
using MySql.Data.MySqlClient;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Dotpay.TaobaoMonitor
{
    internal class LoseTransactionRevalidator
    {
        private static bool started;
        private static readonly string MysqlConnectionString = ConfigurationManagerWrapper.GetDBConnectionString("taobaodb");
        private static readonly string RabbitMqConnectionString = ConfigurationManagerWrapper.GetDBConnectionString("messageQueueServerConnectString");
        private const string RippleValidateExchangeName = "__RippleValidate_Exchange";
        private const string RippleValidateQueue = "__RippleValidate_LedgerIndex_Tx";
        private static LedgerIndexRpcClient ledgerIndexRpcClient;
        private static ValidatorRpcClient validatorRpcClient;

        public static void Start()
        {
            if (started) return;

            var thread = new Thread(() =>
            {
                var factory = new ConnectionFactory() { Uri = LoseTransactionRevalidator.RabbitMqConnectionString, AutomaticRecoveryEnabled = true };
                var connection = factory.CreateConnection();
                ledgerIndexRpcClient = new LedgerIndexRpcClient(connection);
                validatorRpcClient = new ValidatorRpcClient(connection);

                while (true)
                {
                    try
                    {
                        var loseTxs = GetLoseTransaction();
                        var completeLedgerIndex = 0L;

                        if (loseTxs.Any())
                        {
                            completeLedgerIndex = ledgerIndexRpcClient.GetLastLedgerIndex();

                            if (completeLedgerIndex != -1)
                            {
                                loseTxs.ForEach(lt =>
                                {
                                    if (lt.tx_lastLedgerSequence <= completeLedgerIndex)
                                    {
                                        var result = validatorRpcClient.ValidateTx(lt.txid);

                                        if (result == 1)
                                        {
                                            //tx已成功
                                            MarkTxSuccess(lt.tid);
                                        }
                                        else if (result == 0)
                                        {
                                            //tx已失败
                                            MarkTxAsInitForNextProccesLoop(lt.tid);
                                        }
                                        else
                                        {
                                            //未决的tx,应等待最后结果
                                        }
                                    }
                                });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("  Exception", ex);
                    }

                    Task.Delay(6 * 1000).Wait();
                }
            });

            thread.Start();
            Log.Info("-->ripple丢失tx验证器启动成功...");
            started = true;
        }

        #region rpc client
        class LedgerIndexRpcClient
        {
            private IConnection connection;
            private IModel channel;
            private string replyQueueName;
            private QueueingBasicConsumer consumer;

            public LedgerIndexRpcClient(IConnection connection)
            {

                this.connection = connection;
                channel = connection.CreateModel();
                replyQueueName = channel.QueueDeclare();
                channel.ExchangeDeclare(LoseTransactionRevalidator.RippleValidateExchangeName, ExchangeType.Direct);
                channel.QueueDeclare(LoseTransactionRevalidator.RippleValidateQueue, true, false, false, null);
                channel.QueueBind(LoseTransactionRevalidator.RippleValidateQueue, LoseTransactionRevalidator.RippleValidateExchangeName, "");
                consumer = new QueueingBasicConsumer(channel);
                channel.BasicConsume(replyQueueName, true, consumer);
            }

            public long GetLastLedgerIndex()
            {
                var corrId = Guid.NewGuid().ToString();
                var props = channel.CreateBasicProperties();
                props.ReplyTo = replyQueueName;
                props.CorrelationId = corrId;

                var message = IoC.Resolve<IJsonSerializer>().Serialize(new GetLastLedgerIndexMessage());
                var messageBytes = Encoding.UTF8.GetBytes(message);
                channel.BasicPublish(LoseTransactionRevalidator.RippleValidateExchangeName, string.Empty, props, messageBytes);

                while (true)
                {
                    BasicDeliverEventArgs ea;
                    if (consumer.Queue.Dequeue(10 * 1000, out ea))
                    {
                        if (ea != null && ea.BasicProperties.CorrelationId == corrId)
                        {
                            return Convert.ToInt64(Encoding.UTF8.GetString(ea.Body));
                        }
                    }
                    return -1;
                }
            }
        }

        class ValidatorRpcClient
        {
            private IConnection connection;
            private IModel channel;
            private string replyQueueName;
            private QueueingBasicConsumer consumer;

            public ValidatorRpcClient(IConnection connection)
            {
                this.connection = connection;
                channel = connection.CreateModel();
                replyQueueName = channel.QueueDeclare();
                channel.ExchangeDeclare(LoseTransactionRevalidator.RippleValidateExchangeName, ExchangeType.Direct);
                channel.QueueDeclare(LoseTransactionRevalidator.RippleValidateQueue, true, false, false, null);
                channel.QueueBind(LoseTransactionRevalidator.RippleValidateQueue, LoseTransactionRevalidator.RippleValidateExchangeName, "");
                consumer = new QueueingBasicConsumer(channel);
                channel.BasicConsume(replyQueueName, true, consumer);
            }

            /// <summary>
            /// 
            /// </summary>
            /// <param name="txid"></param>
            /// <returns>-1 超时，1 true, 0 false</returns>
            public int ValidateTx(string txid)
            {
                var corrId = Guid.NewGuid().ToString();
                var props = channel.CreateBasicProperties();
                props.ReplyTo = replyQueueName;
                props.CorrelationId = corrId;

                var message = IoC.Resolve<IJsonSerializer>().Serialize(new ValidateTxMessage(txid));
                var messageBytes = Encoding.UTF8.GetBytes(message);
                channel.BasicPublish(LoseTransactionRevalidator.RippleValidateExchangeName, string.Empty, props, messageBytes);

                while (true)
                {
                    BasicDeliverEventArgs ea;
                    if (consumer.Queue.Dequeue(10 * 1000, out ea))
                    {
                        if (ea != null && ea.BasicProperties.CorrelationId == corrId)
                        {
                            return Convert.ToInt32(Encoding.UTF8.GetString(ea.Body));
                        }
                    }
                    return -1;
                }
            }
        }

        #endregion

        #region message
        [Serializable]
        class GetLastLedgerIndexMessage
        {
            public string Command
            {
                get { return "GetLastCompleteLedgerIndex"; }
            }
        }

        [Serializable]
        class ValidateTxMessage
        {
            public ValidateTxMessage(string txId)
            {
                this.TxId = txId;
            }

            public string Command
            {
                get { return "ValidateTx"; }
            }

            public string TxId { get; set; }
        }
        #endregion

        #region private method
        private static MySqlConnection OpenConnection()
        {
            MySqlConnection connection = new MySqlConnection(MysqlConnectionString);
            connection.Open();
            return connection;
        }
        //获取已提交了4分钟，仍然没有结果的数据
        //已提交但从未submit过的，不会重复检测并再次提交，可防止多发IOU
        private static IEnumerable<TaobaoAutoDeposit> GetLoseTransaction()
        {
            const string sql =
                "SELECT tid,amount,has_buyer_message,taobao_status,ripple_address,ripple_status,txid,memo,first_submit_at,tx_lastLedgerSequence,retry_Counter" +
                "  FROM taobao " +
                " WHERE taobao_status=@taobao_status AND ripple_status=@ripple_status AND first_submit_at<>null AND first_submit_at<@submit_at";
            try
            {
                using (var conn = OpenConnection())
                {
                    var tradesInDb = conn.Query<TaobaoAutoDeposit>(sql, new
                    {
                        taobao_status = "WAIT_SELLER_SEND_GOODS",
                        ripple_status = RippleTransactionStatus.Submited,
                        submit_at = DateTime.Now.AddMinutes(-4)
                    });

                    return tradesInDb;
                }
            }
            catch (Exception ex)
            {
                Log.Error("GetLoseTransaction Exception", ex);
                return null;
            }
        }

        private static int MarkTxSuccess(long tid)
        {
            const string sql =
              "UPDATE taobao SET ripple_status=@ripple_status_new " +
              " WHERE tid=@tid AND taobao_status=@taobao_status AND ripple_status=@ripple_status_old";
            try
            {
                using (var conn = OpenConnection())
                {
                    return conn.Execute(sql, new { tid = tid, ripple_status_new = RippleTransactionStatus.Successed, taobao_status = "WAIT_SELLER_SEND_GOODS", ripple_status_old = RippleTransactionStatus.Submited });
                }
            }
            catch (Exception ex)
            {
                Log.Error("MarkTxSuccess Exception", ex);
                return 0;
            }
        }

        //标记为初始状态，可自动进入下次处理过程
        private static int MarkTxAsInitForNextProccesLoop(long tid)
        {
            const string sql =
              "UPDATE taobao SET ripple_status=@ripple_status_new,txid='',tx_lastLedgerSequence=0,first_submit_at=null" +
              " WHERE tid=@tid AND taobao_status=@taobao_status AND ripple_status=@ripple_status_old";
            try
            {
                using (var conn = OpenConnection())
                {
                    return conn.Execute(sql, new { tid = tid, ripple_status_new = RippleTransactionStatus.Init, taobao_status = "WAIT_SELLER_SEND_GOODS", ripple_status_old = RippleTransactionStatus.Submited });
                }
            }
            catch (Exception ex)
            {
                Log.Error("MarkTxSuccess Exception", ex);
                return 0;
            }
        }

        #endregion

    }
}