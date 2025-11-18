using System.Linq;
using DTO = Backend.DTOs;
using DAL = Backend.Relational.DAL;
using Riok.Mapperly.Abstractions;
using Backend.Database.Relational.Entities;

namespace Backend.Mappers;

[Mapper]
public static partial class WorkflowTaskMapper
{
    public static partial IQueryable<DTO.WorkflowTaskInfo> ProjectToWorkflowTaskInfoDto(
        this IQueryable<TaskSchema> workflowTasks);
}
