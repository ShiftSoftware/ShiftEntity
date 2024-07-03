using Microsoft.OData.UriParser;
using ShiftSoftware.ShiftEntity.Model.Dtos;
using System.Linq;

namespace ShiftSoftware.ShiftEntity.Web.Services;

internal class SoftDeleteCollectionConstantVisitor : QueryNodeVisitor<CollectionConstantNode>
{
    private SoftDeleteQueryNodeVisitor _softDeleteQueryNodeVisitor;

    public SoftDeleteCollectionConstantVisitor(SoftDeleteQueryNodeVisitor softDeleteQueryNodeVisitor)
    {
        this._softDeleteQueryNodeVisitor = softDeleteQueryNodeVisitor;
    }

    public override CollectionConstantNode Visit(CollectionConstantNode nodeIn)
    {
        var newCollection = nodeIn.Collection.Select(x => x.Accept(this._softDeleteQueryNodeVisitor));

        return new CollectionConstantNode(
            newCollection,
            "(" + string.Join(",", newCollection.Select(y => ((ConstantNode)y).Value)) + ")",
            nodeIn.CollectionType
        );
    }
}

internal class SoftDeleteQueryNodeVisitor : QueryNodeVisitor<SingleValueNode>
{
    public bool IsFilteringByIsDeleted { get; private set; }

    public override SingleValueNode Visit(ConstantNode nodeIn)
    {
        return nodeIn;
    }

    public override SingleValueNode Visit(BinaryOperatorNode nodeIn)
    {
        var visitedLeft = nodeIn.Left is BinaryOperatorNode leftBinary ?
            Visit(leftBinary) :
            nodeIn.Left.Accept(this);

        var visitedRight = nodeIn.Right is BinaryOperatorNode rightBinary ?
            Visit(rightBinary) :
            nodeIn.Right.Accept(this);

        return new BinaryOperatorNode(
            nodeIn.OperatorKind,
            visitedLeft,
            visitedRight
        );
    }

    public override SingleValueNode Visit(ConvertNode nodeIn)
    {
        return nodeIn.Source.Accept(this);
    }

    public override SingleValueNode Visit(InNode nodeIn)
    {
        return new InNode(
            nodeIn.Left.Accept(this),
            nodeIn.Right.Accept(new SoftDeleteCollectionConstantVisitor(this))
        );
    }

    public override SingleValueNode Visit(SingleValuePropertyAccessNode nodeIn)
    {
        if (nodeIn.Property.Name.Equals(nameof(ShiftEntityDTOBase.IsDeleted)))
            this.IsFilteringByIsDeleted = true;

        return nodeIn;
    }

    public override SingleValueNode Visit(SingleValueFunctionCallNode nodeIn)
    {
        return new SingleValueFunctionCallNode(
            nodeIn.Name,
            nodeIn.Parameters.Select(x => x.Accept(this)),
            nodeIn.TypeReference
        );
    }

    public override SingleValueNode Visit(AnyNode nodeIn)
    {
        nodeIn.Body = nodeIn.Body.Accept(this);

        return nodeIn;
    }
}
