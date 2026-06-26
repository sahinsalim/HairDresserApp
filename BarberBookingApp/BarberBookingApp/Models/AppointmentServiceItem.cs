namespace BarberBookingApp.Models;

public class AppointmentServiceItem
{
    public int Id { get; set; }

    public int AppointmentId { get; set; }
    public Appointment? Appointment { get; set; }

    public int ServiceTypeId { get; set; }
    public ServiceType? ServiceType { get; set; }
}
