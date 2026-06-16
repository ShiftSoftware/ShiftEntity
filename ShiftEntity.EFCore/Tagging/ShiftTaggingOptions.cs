using ShiftSoftware.TypeAuth.Core.Actions;

namespace ShiftSoftware.ShiftEntity.EFCore.Tagging;

/// <summary>
/// Configuration passed to <c>AddShiftTagging</c>. Holds the programmer-supplied
/// action-tree node that gates tag-vocabulary endpoints, plus behavior toggles for
/// unknown-tag handling and join-table naming.
/// </summary>
public class ShiftTaggingOptions
{
    /// <summary>
    /// True when the application has opted into tagging via
    /// <see cref="ShiftTaggingServiceCollectionExtensions.AddShiftTagging{TDbContext}(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{ShiftTaggingOptions})"/>.
    /// The framework reads this to decide whether to include the <c>Tag</c> entity in
    /// the EF model, auto-wire many-to-many join tables for taggable entities, and run
    /// the upsert-on-save pipeline. Setter is internal so only the framework can flip it.
    /// </summary>
    public bool Enabled { get; internal set; }

    /// <summary>
    /// The action-tree node whose Read/Write/Delete access levels gate the tag
    /// vocabulary endpoints. Each microservice supplies its own.
    /// </summary>
    public ReadWriteDeleteAction? Action { get; set; }

    /// <summary>
    /// Default schema for the auto-wired many-to-many join tables.
    /// Per-entity override via <see cref="Core.Tagging.ShiftTagTableAttribute"/>.
    /// </summary>
    public string? JoinTableSchema { get; set; }

    /// <summary>
    /// Suffix used when generating join-table names. Default <c>"Tags"</c>.
    /// </summary>
    public string JoinTableSuffix { get; set; } = "Tags";

    /// <summary>
    /// Route prefix for the auto-registered tag CRUD endpoints. Default <c>"api/tags"</c>.
    /// </summary>
    public string EndpointPrefix { get; set; } = "api/tags";

    /// <summary>
    /// Skip endpoint registration so the programmer can wire their own. The repository
    /// is still registered.
    /// </summary>
    public bool SkipEndpointRegistration { get; set; }
}
