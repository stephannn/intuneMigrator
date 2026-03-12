using System;
using System.ComponentModel.DataAnnotations;

namespace intuneMigratorApi.Models;


public class MigrationStatusEntity
{
    [Key]
    public int Id { get; set; }
    public string Name { get; set; } = null!;
}

public enum MigrationStatus
{
    Processing = 1,
    Received = 2,
    RemovedFromSource = 3,
    WipeRequested = 4,
    AddedToDestination = 5,
    Success = 6,
    Failed = 7
}