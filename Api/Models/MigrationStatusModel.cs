using System;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace intuneMigratorApi.Models
{
    [PrimaryKey(nameof(Id), nameof(Status))]
    public class MigrationStatusModel
    {
        public Guid Id { get; set; }
        public MigrationStatus Status { get; set; }
        public string? SerialNumber { get; set; }
        public string? DeviceName { get; set; }
        public string? Message { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public string Username { get; set; } = string.Empty;
        public string GroupTag { get; set; } = string.Empty;
        public bool Debug { get; set; } = false;
    }
}
