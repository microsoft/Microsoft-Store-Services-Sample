//-----------------------------------------------------------------------------
// ServerDBContext.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
//-----------------------------------------------------------------------------

using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace MicrosoftStoreServicesSample.PersistentDB
{
    public class ServerDBContext : DbContext
    {
        public ServerDBContext(DbContextOptions<ServerDBContext> options)
            : base(options)
        { }

        //  These dbsets will contain data that we want to be shared across our
        //  servers, so we store them in a persistent SQL database in Azure.  The sample
        //  by default looks for the app settings ConnectionString "GameServicePersistentDB"
        //  if not found, then it will default to use an in-memory database for simplicity
        //  But all deployed code should be using a real DB for these tables to prevent
        //  data loss and unnecessary network traffic.
        public DbSet<PendingConsumeRequest> PendingConsumeRequests { get; set; }
        public DbSet<UserConsumableBalance> UserBalances { get; set; }
        public DbSet<ClawbackQueueItem> ClawbackQueue { get; set; }
        public DbSet<ClawbackActionItem> ClawbackActionItems { get; set; }

        public override int SaveChanges()
        {
            foreach (var entry in ChangeTracker.Entries()
                    .Where(e => e.Metadata.IsOwned() && e.State == EntityState.Added))
            {
                var ownership = entry.Metadata.FindOwnership();
                var parentKey = ownership.Properties
                                 .Select(p => entry.Property(p.Name).CurrentValue).ToArray();
                var parent = this.Find(ownership.PrincipalEntityType.ClrType, parentKey);
                if (parent != null)
                {
                    var parentEntry = this.Entry(parent);
                    if (parentEntry.State != EntityState.Added)
                    {
                        entry.State = EntityState.Modified;
                    }
                }
            }
            return base.SaveChanges();
        }
    }
}
