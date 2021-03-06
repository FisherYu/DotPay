﻿using System;
using System.Threading.Tasks;
using Dotpay.Common.Enum;
using Orleans;
using Orleans.Concurrency;

namespace Dotpay.Actor.Service
{
    public interface IRefundTransactionManager : IGrainWithIntegerKey
    {
        Task Receive(MqMessage message);
    }

    [Immutable]
    [Serializable]
    public class RefundTransactionMessage : MqMessage
    {
        public RefundTransactionMessage(Guid refundTransactionId, Guid transactionId, RefundTransactionType refundTransactionType)
        {
            this.RefundTransactionId = refundTransactionId;
            this.TransactionId = transactionId;
            this.RefundTransactionType = refundTransactionType;
        }

        public Guid RefundTransactionId { get; private set; }
        public Guid TransactionId { get; private set; }
        public RefundTransactionType RefundTransactionType { get; private set; }
    }
}
