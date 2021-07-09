//-----------------------------------------------------------------------------
// UserConsumableBalance.cs
//
// Xbox Advanced Technology Group (ATG)
// Copyright (C) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License file under the project root for
// license information.
//-----------------------------------------------------------------------------

using System.ComponentModel.DataAnnotations;

namespace MicrosoftStoreServicesSample
{
    //  This is a very simple implementation to demonstrate
    //  tracking the values of a user on the server to work
    //  with the consume and retry pending consume functionality
    public class UserConsumableBalance
    {
        //  Lookup key is UserId:ProductId which should give us a
        //  unique string to lookup a specific product balance on
        //  a per-user basis
        [Key]
        public string LookupKey { get; set; }
        public string UserId { get; set; }
        public string ProductId { get; set; }
        public int Quantity { get; set; }
    }
}
