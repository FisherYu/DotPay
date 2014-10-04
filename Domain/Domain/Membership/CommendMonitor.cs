﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FC.Framework.Domain;
using FC.Framework;
using DotPay.Domain.Events;
using DotPay.Common;
using FC.Framework.Utilities;
using FC.Framework.Repository;
using DotPay.Domain.Repository;

namespace DotPay.Domain
{
    public class CommendMonitor : IEventHandler<UserCommendSuccess>
    {
        public void Handle(UserCommendSuccess @event)
        {
            var commendUser = IoC.Resolve<IRepository>().FindById<User>(@event.CommendBy);

            commendUser.CommendIncrease();
        }
    }
}