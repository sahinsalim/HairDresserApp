using BarberBookingApp.Models;

namespace BarberBookingApp.Services;

public interface IAppointmentService
{
    Task<List<TimeSpan>> GetAvailableSlotsAsync(DateOnly date, int serviceTypeId);
    Task<List<TimeSpan>> GetAvailableSlotsAsync(DateOnly date, IReadOnlyCollection<int> serviceTypeIds);

    Task<AppointmentResult> CreateAppointmentAsync(int customerId, int serviceTypeId, DateTime startTime);
    Task<AppointmentResult> CreateAppointmentAsync(int customerId, IReadOnlyCollection<int> serviceTypeIds, DateTime startTime);

    Task<List<Appointment>> GetCustomerAppointmentsAsync(int customerId);

    Task<List<Appointment>> GetAllAppointmentsAsync(DateOnly? date = null, AppointmentStatus? status = null);

    Task<AppointmentResult> CancelAppointmentAsync(int appointmentId, string cancelledBy, string? reason);

    DateOnly GetMinBookingDate();

    DateOnly GetMaxBookingDate();
}

public record AppointmentResult(bool Success, string? ErrorMessage, Appointment? Appointment = null);
