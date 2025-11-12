using System.Linq;
using DTO = Backend.DTOs;
using DAL = Backend.Relational.DAL;
using Riok.Mapperly.Abstractions;

namespace Backend.Mappers;

[Mapper]
public static partial class WorkflowTaskMapper
{
    public static partial IQueryable<DTO.WorkflowTaskInfo> ProjectToWorkflowTaskInfoDto(
        this IQueryable<DAL.TaskSchema> workflowTasks);
}
