﻿using DotPay.Domain;
using NHibernate.Mapping.ByCode;
using NHibernate.Mapping.ByCode.Conformist;
using DotPay.Common;

namespace DotPay.Persistence
{
    public class ArticleMap : BaseClassMapping<Article>
    {
        public ArticleMap()
        {
            Id(u => u.ID, map => map.Generator(Generators.Identity));

            Property(x => x.Lang, map => { map.NotNullable(true); });
            Property(x => x.IsTop, map => { map.NotNullable(true); });
            Property(x => x.Title, map => { map.NotNullable(true); map.Length(200); });
            Property(x => x.Category, map => { map.NotNullable(true); });
            Property(x => x.Content, map => { map.NotNullable(true); map.Length(60000); });
            Property(x => x.CreateBy, map => { map.NotNullable(true); });
            Property(x => x.CreateAt, map => { map.NotNullable(true); });
            Property(x => x.UpdateAt, map => { map.NotNullable(true); });
            Property(x => x.UpdateBy, map => { map.NotNullable(true); });
            Version(x => x.Version, map => { });
        }
    }
}