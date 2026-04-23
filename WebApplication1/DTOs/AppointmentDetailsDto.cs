namespace WebApplication1.DTOs;

public class AppointmentDetailsDto : AppointmentListDto
{
    public string DoctorFullName { get; set; } = string.Empty;
    public string DoctorSpecialization { get; set; } = string.Empty;
    public string? InternalNotes { get; set; }
    public DateTime CreatedAt { get; set; }
}