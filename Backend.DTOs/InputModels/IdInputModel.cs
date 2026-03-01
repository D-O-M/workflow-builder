using System.ComponentModel.DataAnnotations;

namespace Backend.DTOs.InputModels;

public class IdInputModel
{
    [Range(1, int.MaxValue)]
    public int Id { get; set; }
}
