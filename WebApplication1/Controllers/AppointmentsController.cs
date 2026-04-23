using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using WebApplication1.DTOs;
using System.Data;

namespace WebApplication1.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AppointmentsController : ControllerBase
{
    private readonly string _connectionString;

    public AppointmentsController(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")!;
    }

    [HttpGet]
    public async Task<IActionResult> GetAppointments([FromQuery] string? status, [FromQuery] string? patientLastName)
    {
        var appointments = new List<AppointmentListDto>();

        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand("""
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, 
                   p.FirstName + ' ' + p.LastName AS PatientFullName, p.Email AS PatientEmail
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON a.IdPatient = p.IdPatient
            WHERE (@Status IS NULL OR a.Status = @Status)
              AND (@LastName IS NULL OR p.LastName = @LastName)
            ORDER BY a.AppointmentDate
            """, connection);

        command.Parameters.AddWithValue("@Status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("@LastName", (object?)patientLastName ?? DBNull.Value);

        await connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            appointments.Add(new AppointmentListDto
            {
                IdAppointment = reader.GetInt32(0),
                AppointmentDate = reader.GetDateTime(1),
                Status = reader.GetString(2),
                Reason = reader.GetString(3),
                PatientFullName = reader.GetString(4),
                PatientEmail = reader.GetString(5)
            });
        }

        return Ok(appointments);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetAppointment(int id)
    {
        await using var connection = new SqlConnection(_connectionString);
        await using var command = new SqlCommand("""
            SELECT a.IdAppointment, a.AppointmentDate, a.Status, a.Reason, a.InternalNotes, a.CreatedAt,
                   p.FirstName + ' ' + p.LastName AS PatientFullName, p.Email,
                   d.FirstName + ' ' + d.LastName AS DoctorFullName, s.Name AS Specialization
            FROM dbo.Appointments a
            JOIN dbo.Patients p ON a.IdPatient = p.IdPatient
            JOIN dbo.Doctors d ON a.IdDoctor = d.IdDoctor
            JOIN dbo.Specializations s ON d.IdSpecialization = s.IdSpecialization
            WHERE a.IdAppointment = @Id
            """, connection);

        command.Parameters.AddWithValue("@Id", id);
        await connection.OpenAsync();
        await using var reader = await command.ExecuteReaderAsync();

        if (!await reader.ReadAsync()) return NotFound(new ErrorResponseDto("Wizyta nie istnieje."));

        return Ok(new AppointmentDetailsDto
        {
            IdAppointment = reader.GetInt32(0),
            AppointmentDate = reader.GetDateTime(1),
            Status = reader.GetString(2),
            Reason = reader.GetString(3),
            InternalNotes = reader.IsDBNull(4) ? null : reader.GetString(4),
            CreatedAt = reader.GetDateTime(5),
            PatientFullName = reader.GetString(6),
            PatientEmail = reader.GetString(7),
            DoctorFullName = reader.GetString(8),
            DoctorSpecialization = reader.GetString(9)
        });
    }

    [HttpPost]
    public async Task<IActionResult> CreateAppointment(CreateAppointmentRequestDto request)
    {
        if (request.AppointmentDate < DateTime.Now) 
            return BadRequest(new ErrorResponseDto("Data wizyty nie może być w przeszłości."));
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 250)
            return BadRequest(new ErrorResponseDto("Powód wizyty jest wymagany i nie może przekraczać 250 znaków."));

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        
        var checkSql = "SELECT (SELECT COUNT(*) FROM Patients WHERE IdPatient = @PId) AS PCount, (SELECT COUNT(*) FROM Doctors WHERE IdDoctor = @DId) AS DCount";
        await using var checkCmd = new SqlCommand(checkSql, connection);
        checkCmd.Parameters.AddWithValue("@PId", request.IdPatient);
        checkCmd.Parameters.AddWithValue("@DId", request.IdDoctor);
        
        using var checkReader = await checkCmd.ExecuteReaderAsync();
        await checkReader.ReadAsync();
        if (checkReader.GetInt32(0) == 0) return BadRequest(new ErrorResponseDto("Pacjent nie istnieje."));
        if (checkReader.GetInt32(1) == 0) return BadRequest(new ErrorResponseDto("Lekarz nie istnieje."));
        await checkReader.CloseAsync();
        
        var conflictSql = "SELECT COUNT(*) FROM Appointments WHERE IdDoctor = @DId AND AppointmentDate = @Date AND Status != 'Cancelled'";
        await using var conflictCmd = new SqlCommand(conflictSql, connection);
        conflictCmd.Parameters.AddWithValue("@DId", request.IdDoctor);
        conflictCmd.Parameters.AddWithValue("@Date", request.AppointmentDate);
        
        if ((int)(await conflictCmd.ExecuteScalarAsync() ?? 0) > 0)
            return Conflict(new ErrorResponseDto("Lekarz ma już zaplanowaną wizytę w tym terminie."));
        
        var insertSql = """
            INSERT INTO Appointments (IdPatient, IdDoctor, AppointmentDate, Status, Reason, CreatedAt)
            VALUES (@PId, @DId, @Date, 'Scheduled', @Reason, GETDATE());
            SELECT CAST(SCOPE_IDENTITY() as int);
            """;
        await using var insertCmd = new SqlCommand(insertSql, connection);
        insertCmd.Parameters.AddWithValue("@PId", request.IdPatient);
        insertCmd.Parameters.AddWithValue("@DId", request.IdDoctor);
        insertCmd.Parameters.AddWithValue("@Date", request.AppointmentDate);
        insertCmd.Parameters.AddWithValue("@Reason", request.Reason);

        var newId = (int)await insertCmd.ExecuteScalarAsync()!;
        return CreatedAtAction(nameof(GetAppointment), new { id = newId }, new { IdAppointment = newId });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteAppointment(int id)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        
        var statusSql = "SELECT Status FROM Appointments WHERE IdAppointment = @Id";
        await using var statusCmd = new SqlCommand(statusSql, connection);
        statusCmd.Parameters.AddWithValue("@Id", id);
        
        var status = await statusCmd.ExecuteScalarAsync();
        if (status == null) return NotFound();
        if (status.ToString() == "Completed") return Conflict(new ErrorResponseDto("Nie można usunąć zakończonej wizyty."));
        
        var deleteSql = "DELETE FROM Appointments WHERE IdAppointment = @Id";
        await using var deleteCmd = new SqlCommand(deleteSql, connection);
        deleteCmd.Parameters.AddWithValue("@Id", id);
        await deleteCmd.ExecuteNonQueryAsync();

        return NoContent();
    }
}