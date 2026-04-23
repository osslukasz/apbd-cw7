namespace WebApplication1.DTOs;

public class UpdateAppointmentRequestDto : CreateAppointmentRequestDto
{
    public string Status { get; set; } = string.Empty;
    public string? InternalNotes { get; set; }
}
