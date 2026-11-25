using Newtonsoft.Json;
using ShiftSoftware.ShiftEntity.Core;
using ShiftSoftware.ShiftEntity.Model;
using ShiftSoftware.TypeAuth.Core;
using Xunit;

namespace ShiftSoftware.ShiftEntity.Tests.DataGrid;

/// <summary>
/// <see cref="GeneralActionTree.DataGridBulkWriteMaxTop"/> on real TypeAuth contexts: what a user's access trees give, and
/// what <see cref="DataGridBulkWriteExtensions"/> does with it.
/// </summary>
public class DataGridBulkWriteMaxTopTests
{
    private const int MinimumAccess = 5;

    // Saved access trees hold this key. A rename silently drops every grant, and takes users down to the minimum.
    [Fact]
    public void The_key_in_saved_access_trees_and_the_name_in_the_tree_editor_stay_as_they_are()
    {
        Assert.Equal("DataGridBulkWriteMaxTop", nameof(GeneralActionTree.DataGridBulkWriteMaxTop));
        Assert.Equal("Data Grid Bulk Write Max Top", GeneralActionTree.DataGridBulkWriteMaxTop.Name);

        var node = TypeAuth().FindActionTreeNode("GeneralActionTree.DataGridBulkWriteMaxTop");

        Assert.Same(GeneralActionTree.DataGridBulkWriteMaxTop, node?.Action);
    }

    [Fact]
    public void A_user_without_the_grant_gets_the_minimum()
    {
        Assert.Equal(MinimumAccess, TypeAuth().GetDataGridBulkWriteMaxTop());
    }

    [Fact]
    public void A_host_that_does_not_register_the_tree_gets_the_minimum()
    {
        var typeAuth = new TypeAuthContextBuilder().AddAccessTree(Grant(50)).Build();

        Assert.Equal(MinimumAccess, typeAuth.GetDataGridBulkWriteMaxTop());
    }

    [Fact]
    public void The_whole_tree_gives_the_maximum()
    {
        var tree = JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            [nameof(GeneralActionTree)] = new[] { Access.Read, Access.Write, Access.Delete, Access.Maximum },
        });

        Assert.Equal(int.MaxValue, TypeAuth(tree).GetDataGridBulkWriteMaxTop());
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(2.9, 2)]
    [InlineData(2000.0, 2000)]
    [InlineData(99_999_999_999, int.MaxValue)]
    public void The_granted_value_is_the_limit_in_whole_rows(double granted, int expected)
    {
        Assert.Equal(expected, TypeAuth(Grant(granted)).GetDataGridBulkWriteMaxTop());
    }

    [Theory]
    [InlineData(10, 40)]
    [InlineData(40, 10)]
    public void The_greatest_value_of_the_users_trees_is_the_limit(int first, int second)
    {
        Assert.Equal(40, TypeAuth(Grant(first), Grant(second)).GetDataGridBulkWriteMaxTop());
    }

    // The two limits are granted apart: reading many rows doesn't let a user write many, nor the other way around.
    [Fact]
    public void The_read_limit_and_the_bulk_write_limit_do_not_affect_each_other()
    {
        var reader = TypeAuth(Grant(2000, nameof(GeneralActionTree.DataGridMaxTop)));
        var writer = TypeAuth(Grant(2000));

        Assert.Equal(MinimumAccess, reader.GetDataGridBulkWriteMaxTop());
        Assert.Equal(2000, reader.AccessValue(GeneralActionTree.DataGridMaxTop));

        Assert.Equal(2000, writer.GetDataGridBulkWriteMaxTop());
        Assert.Equal(MinimumAccess, writer.AccessValue(GeneralActionTree.DataGridMaxTop));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    public void A_custom_grid_can_write_up_to_the_limit(int rowCount)
    {
        TypeAuth(Grant(50)).EnsureDataGridBulkWriteMaxTop(rowCount);
    }

    [Fact]
    public void A_custom_grid_that_writes_more_is_refused_with_a_400_that_names_both_numbers()
    {
        var typeAuth = TypeAuth(Grant(50));

        var refusal = Assert.Throws<ShiftEntityException>(() => typeAuth.EnsureDataGridBulkWriteMaxTop(51));

        Assert.Equal(400, refusal.HttpStatusCode);
        Assert.Equal("Bulk Write Limit Exceeded", refusal.Message.Title);
        Assert.Contains("(51)", refusal.Message.Body);
        Assert.Contains("maximum of 50 ", refusal.Message.Body);
    }

    // The read limit takes 0 as "no limit". Here 0 allows nothing.
    [Fact]
    public void A_limit_of_0_allows_no_row()
    {
        var typeAuth = TypeAuth(Grant(0));

        typeAuth.EnsureDataGridBulkWriteMaxTop(0);
        Assert.Throws<ShiftEntityException>(() => typeAuth.EnsureDataGridBulkWriteMaxTop(1));
    }

    internal static string Grant(double value, string action = nameof(GeneralActionTree.DataGridBulkWriteMaxTop))
        => JsonConvert.SerializeObject(new Dictionary<string, object>
        {
            [nameof(GeneralActionTree)] = new Dictionary<string, object> { [action] = (decimal)value },
        });

    internal static TypeAuthContext TypeAuth(params string[] accessTrees)
    {
        var builder = new TypeAuthContextBuilder().AddActionTree<GeneralActionTree>();

        foreach (var accessTree in accessTrees)
            builder.AddAccessTree(accessTree);

        return builder.Build();
    }
}
