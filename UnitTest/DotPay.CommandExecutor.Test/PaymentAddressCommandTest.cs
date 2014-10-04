﻿using System;
using Xunit;
using FC.Framework;
using FC.Framework.Autofac;
using FC.Framework.CouchbaseCache;
using FC.Framework.Log4net;
using FC.Framework.NHibernate;
using FC.Framework.Repository;
using DotPay.Command;
using DotPay.Common;
using DotPay.Persistence;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using DotPay.Domain.Repository;
using DotPay.Domain;
using FC.Framework.Utilities;

namespace DotPay.CommandExecutor.Test
{
    public class PaymentAddressCommandTest
    {
        ICommandBus commandBus;

        public PaymentAddressCommandTest()
        {
            TestEnvironment.Init();
            this.commandBus = IoC.Resolve<ICommandBus>();
        }


        [Fact]
        public void TestCreatePaymentAddress()
        {
            var userID = new Random().Next(1, 10);
            var paymentAddress = "RrXzSnqQCqPigKdDDyc5oNDppgdhsg4oBc";

            var cmd = new CreatePaymentAddress(userID, paymentAddress, CurrencyType.BTC);

            Assert.DoesNotThrow(delegate
            {
                this.commandBus.Send(cmd);
            });
        }
    }
}
