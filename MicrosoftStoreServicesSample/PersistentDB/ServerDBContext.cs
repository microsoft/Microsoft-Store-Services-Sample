//-----------------------------------------------------------------------------
// ServerDBContext.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License file under the project root for
// license information.
//-----------------------------------------------------------------------------

using Microsoft.EntityFrameworkCore;

namespace MicrosoftStoreServicesSample.PersistentDB
{
    public class ServerDBContext : DbContext
    {
        public ServerDBContext(DbContextOptions<ServerDBContext> options)
            : base(options)
        { }

        //  These DbSets will contain data that we want to be shared across our
        //  servers, so we store them in a persistent SQL database in Azure.  The sample
        //  by default looks for the app settings ConnectionString "GameServicePersistentDB"
        //  if not found, then it will default to use an in-memory database for simplicity
        //  But all deployed code should be using a real DB for these tables to prevent
        //  data loss and unnecessary network traffic.
        public DbSet<UserConsumableBalance>       UserBalances { get; set; }
        public DbSet<ClawbackQueueItem>           ClawbackQueue { get; set; }
        public DbSet<PendingConsumeRequest>       PendingConsumeRequests { get; set; }
        public DbSet<CompletedConsumeTransaction> CompletedConsumeTransactions { get; set; } 
    }
}
