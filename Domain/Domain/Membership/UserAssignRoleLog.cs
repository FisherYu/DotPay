﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FC.Framework.Domain;
using DotPay.Domain.Events;
using FC.Framework;

namespace DotPay.Domain
{
    public class UserAssignRoleLog : OperateLog
    {
        #region ctor
        protected UserAssignRoleLog() { }
        public UserAssignRoleLog(int userID, string memo, int operatorID, string ip)
            : base(userID, string.Empty, memo, operatorID, ip)
        { }
        #endregion
    }
}