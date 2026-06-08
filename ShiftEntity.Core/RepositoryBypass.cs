using System;

namespace ShiftSoftware.ShiftEntity.Core;

/// <summary>
/// What a repository call opts out of. The default everywhere is <see cref="None"/> — data-level access and the
/// global repository filters both apply; a bypass is the explicit exception (reload-after-save, internal plumbing,
/// trusted background work). This is the named replacement for the positional
/// <c>disableDefaultDataLevelAccess</c>/<c>disableGlobalFilters</c> bool pair: the convenience overloads taking a
/// <see cref="RepositoryBypass"/> forward into the original bool-taking virtual methods, so repositories that
/// override those keep receiving every call (see the forwarding notes on <c>ShiftRepository</c>).
/// </summary>
[Flags]
public enum RepositoryBypass
{
    /// <summary>Bypass nothing — data-level access and global repository filters apply (the default).</summary>
    None = 0,

    /// <summary>
    /// Skip data-level access entirely: the declared v2 policy (query filter and row authorization) and the legacy
    /// default filters/row checks alike. Equivalent to <c>disableDefaultDataLevelAccess: true</c>.
    /// </summary>
    DataLevelAccess = 1,

    /// <summary>Skip the entity's global repository filters. Equivalent to <c>disableGlobalFilters: true</c>.</summary>
    GlobalFilters = 2,

    /// <summary>Skip both — the fully unfiltered, unchecked access used by internal plumbing.</summary>
    All = DataLevelAccess | GlobalFilters,
}
