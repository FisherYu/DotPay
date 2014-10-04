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
using DotPay.Domain.Exceptions;

namespace DotPay.Domain
{
    public class BTCDepositProcessLog : OperateLog
    {
        #region ctor
        protected BTCDepositProcessLog() { }

        public BTCDepositProcessLog(int depositID, string depositUniqueID, int processorID, string memo) : base(depositID, depositUniqueID, memo, processorID, string.Empty) { }
        #endregion
    }
}