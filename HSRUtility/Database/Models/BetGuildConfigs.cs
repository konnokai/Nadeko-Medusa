﻿namespace HSRUtility.Database.Models
{
    public class PlayerIdLink : DbEntity
    {
        public ulong UserId { get; set; }
        public string PlayerId { get; set; } = "";
    }
}
