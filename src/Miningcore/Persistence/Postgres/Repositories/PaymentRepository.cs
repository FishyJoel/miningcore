/*
Copyright 2017 Coin Foundry (coinfoundry.org)
Authors: Oliver Weichhold (oliver@weichhold.com)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
associated documentation files (the "Software"), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial
portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Concurrent;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using AutoMapper;
using Dapper;
using Miningcore.Extensions;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Model.Projections;
using Miningcore.Persistence.Repositories;
using NLog;

namespace Miningcore.Persistence.Postgres.Repositories
{
    public class PaymentRepository : IPaymentRepository
    {
        public PaymentRepository(IMapper mapper)
        {
            this.mapper = mapper;
        }

        private readonly IMapper mapper;
        private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

        public async Task InsertAsync(IDbConnection con, IDbTransaction tx, Payment payment)
        {
            logger.LogInvoke();

            var mapped = mapper.Map<Entities.Payment>(payment);

            const string query = "INSERT INTO payments(poolid, coin, address, amount, transactionconfirmationdata, created) " +
                "VALUES(@poolid, @coin, @address, @amount, @transactionconfirmationdata, @created)";

            await con.ExecuteAsync(query, mapped, tx);
        }
        
        public async Task<Payment[]> PagePaymentsAsync(IDbConnection con, string poolId, string address, int page, int pageSize)
        {
            logger.LogInvoke(new[] { poolId });

            var query = "SELECT * FROM payments WHERE poolid = @poolid ";

            if(!string.IsNullOrEmpty(address))
                query += " AND address = @address ";

            query += "ORDER BY created DESC OFFSET @offset FETCH NEXT (@pageSize) ROWS ONLY";

            return (await con.QueryAsync<Entities.Payment>(query, new { poolId, address, offset = page * pageSize, pageSize }))
                .Select(mapper.Map<Payment>)
                .ToArray();
        }

        public async Task<AmountByDate[]> PageMinerPaymentsByDayAsync(IDbConnection con, string poolId, string address, int page, int pageSize)
        {
            logger.LogInvoke(new[] { poolId });

            const string query = "SELECT SUM(amount) AS amount, date_trunc('day', created) AS date FROM payments WHERE poolid = @poolid " +
                "AND address = @address " +
                "GROUP BY date " +
                "ORDER BY date DESC OFFSET @offset FETCH NEXT (@pageSize) ROWS ONLY";

            return (await con.QueryAsync<AmountByDate>(query, new { poolId, address, offset = page * pageSize, pageSize }))
                .ToArray();
        }

        public async Task<PoolState> GetPoolState(IDbConnection con, string poolId)
        {
            logger.LogInvoke();

            const string query = "SELECT poolid, hashvalue, lastpayout FROM poolstate WHERE poolid = @poolId";

            return await con.QuerySingleOrDefaultAsync<PoolState>(query, new { poolId });
        }

        public async Task SetPoolState(IDbConnection con, PoolState state)
        {
            logger.LogInvoke();

            var mapped = mapper.Map<Entities.PoolState>(state);

            const string query = @"INSERT INTO poolstate as ps(poolid, hashvalue, lastpayout) VALUES (@poolId, @hashValue, @lastpayout)
                ON CONFLICT (poolid)
                DO UPDATE SET hashvalue = (CASE WHEN EXCLUDED.hashvalue ISNULL OR EXCLUDED.hashvalue=0 THEN ps.hashvalue ELSE EXCLUDED.hashvalue END), lastpayout = COALESCE(EXCLUDED.lastpayout, ps.lastpayout)";

            await con.ExecuteAsync(query, mapped);
        }
    }
}
