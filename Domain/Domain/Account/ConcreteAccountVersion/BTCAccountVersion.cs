﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FC.Framework;
using FC.Framework.Domain;
using DotPay.Domain.Events;
using DotPay.Domain.Exceptions;
using DotPay.Common;

namespace DotPay.Domain
{
    public class BTCAccountVersion : AccountVersion
    {
        #region ctor
        protected BTCAccountVersion() { }

        public BTCAccountVersion(int userID, int accountID, decimal amount,
                              decimal balance, decimal locked, decimal @in, decimal @out,
                              int modifyID, int modifyType)
            : base(userID, accountID, amount, balance, locked, @in, @out, modifyID, modifyType) { }


        public BTCAccountVersion(int userID, int accountID, decimal amount,
                             decimal balance, decimal locked, decimal @in, decimal @out,
                             string depositCode, int modifyType)
            : base(userID, accountID, amount, balance, locked, @in, @out, depositCode, modifyType) { }
        #endregion
    }
}