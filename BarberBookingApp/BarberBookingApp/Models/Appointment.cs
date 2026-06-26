namespace BarberBookingApp.Models;

public class Appointment
{
    public int Id { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public int ServiceTypeId { get; set; }
    public ServiceType? ServiceType { get; set; }
    public List<AppointmentServiceItem> ServiceItems { get; set; } = new();

    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Confirmed;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? CancelledAt { get; set; }

    public string? CancelReason { get; set; }

    public string CancelledBy { get; set; } = string.Empty;
}
