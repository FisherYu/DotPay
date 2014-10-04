﻿using FC.Framework.NHibernate;
using FC.Framework.Repository;
using FC.Framework.Utilities;
using DotPay.Domain;
using DotPay.Domain.Repository;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DotPay.Persistence.Repository
{
    public class UserRepository : FC.Framework.NHibernate.Repository, IUserRepository
    {
        public UserRepository(IDatabaseFactory databaseFactory)
            : base(databaseFactory) { }

        public User FindByEmail(string email)
        {
            Check.Argument.IsNotEmpty(email, "email");
            var users = this._session.QueryOver<User>().Where(user => user.Email == email);
            return users.SingleOrDefault();
        }
    }
}