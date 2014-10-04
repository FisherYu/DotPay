﻿using FC.Framework.Repository;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DotPay.Domain.Repository
{
    public interface IUserRepository : IRepository
    {
        User FindByEmail(string email);
    }
}