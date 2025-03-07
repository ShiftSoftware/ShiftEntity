﻿using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using ShiftSoftware.ShiftEntity.Model.Replication;
using System.Collections.Generic;


namespace ShiftSoftware.ShiftEntity.Model.Replication.IdentityModels;

public class CompanyBranchModel : ReplicationModel
{
    public string Name { get; set; } = default!;
    public string? Phone { get; set; }
    public List<TaggedTextDTO> Phones { get; set; } = new();
    public string? ShortPhone { get; set; }
    public string? Email { get; set; }
    public List<TaggedTextDTO> Emails { get; set; } = new();
    public string? Address { get; set; }
    public string? IntegrationId { get; set; } = default!;
    public string? ShortCode { get; set; }

    public Location? Location { get; set; }

    public string? Photos { get; set; }
    public string? WorkingHours { get; set; }
    public string? WorkingDays { get; set; }

    public bool BuiltIn { get; set; }

    public CityCompanyBranchModel City { get; set; }
    public CompanyModel Company { get; set; }

    //Partition keys
    public string BranchID { get; set; } = default!;
    public string ItemType { get; set; } = default!;
    public Dictionary<string, CustomField>? CustomFields { get; set; }
}