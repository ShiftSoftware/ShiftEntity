using AutoMapper;
using ShiftSoftware.ShiftEntity.Core.Tagging;
using ShiftSoftware.ShiftEntity.Model.Dtos.Tagging;

namespace ShiftSoftware.ShiftEntity.EFCore.Tagging;

/// <summary>
/// Default AutoMapper mappings for <see cref="Tag"/> ↔ <see cref="TagDTO"/> and
/// <see cref="Tag"/> → <see cref="TagListDTO"/>. Registered automatically by
/// <c>AddShiftTagging</c> when an <c>IMapper</c> is present in DI.
///
/// The DTO → Entity direction ignores all writable properties on the entity
/// navigation side; tag attach/detach is handled by <see cref="TaggingPipeline"/>
/// during upsert, not by AutoMapper.
/// </summary>
public class ShiftTaggingAutoMapperProfile : Profile
{
    public ShiftTaggingAutoMapperProfile()
    {
        CreateMap<Tag, TagDTO>()
            .ForMember(d => d.ID, opt => opt.MapFrom(src => src.ID.ToString()))
            .ForMember(d => d.CreatedByUserID, opt => opt.MapFrom(src => src.CreatedByUserID == null ? null : src.CreatedByUserID.ToString()))
            .ForMember(d => d.LastSavedByUserID, opt => opt.MapFrom(src => src.LastSavedByUserID == null ? null : src.LastSavedByUserID.ToString()));

        CreateMap<Tag, TagListDTO>()
            .ForMember(d => d.ID, opt => opt.MapFrom(src => src.ID.ToString()));

        CreateMap<TagDTO, Tag>()
            .ForMember(d => d.ID, opt => opt.Ignore())
            .ForMember(d => d.CreateDate, opt => opt.Ignore())
            .ForMember(d => d.LastSaveDate, opt => opt.Ignore())
            .ForMember(d => d.LastReplicationDate, opt => opt.Ignore())
            .ForMember(d => d.CreatedByUserID, opt => opt.Ignore())
            .ForMember(d => d.LastSavedByUserID, opt => opt.Ignore());
    }
}
