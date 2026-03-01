using System.ComponentModel.DataAnnotations;

namespace Backend.Models.InputModels;

public class IdInputModel
{
    [Range(1, int.MaxValue)]
    public int Id { get; set; }
}
