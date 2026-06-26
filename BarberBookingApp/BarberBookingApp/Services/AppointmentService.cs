using BarberBookingApp.Data;
using BarberBookingApp.Models;
using Microsoft.EntityFrameworkCore;

namespace BarberBookingApp.Services;

public class AppointmentService : IAppointmentService
{
    private readonly AppDbContext _db;
    private readonly ISmsService _smsService;
    private readonly int _maxBookingDaysAhead;
    private readonly int _slotStepMinutes;

    public AppointmentService(AppDbContext db, ISmsService smsService, IConfiguration configuration)
    {
        _db = db;
        _smsService = smsService;
        _maxBookingDaysAhead = configuration.GetValue("AppSettings:MaxBookingDaysAhead", 7);
        _slotStepMinutes = configuration.GetValue("AppSettings:SlotStepMinutes", 15);
    }

    public DateOnly GetMinBookingDate() => DateOnly.FromDateTime(DateTime.Now);

    public DateOnly GetMaxBookingDate() => DateOnly.FromDateTime(DateTime.Now).AddDays(_maxBookingDaysAhead);

    public Task<List<TimeSpan>> GetAvailableSlotsAsync(DateOnly date, int serviceTypeId) =>
        GetAvailableSlotsAsync(date, new[] { serviceTypeId });

    public async Task<List<TimeSpan>> GetAvailableSlotsAsync(DateOnly date, IReadOnlyCollection<int> serviceTypeIds)
    {
        if (date < GetMinBookingDate() || date > GetMaxBookingDate())
        {
            return new List<TimeSpan>();
        }

        var selectedServiceIds = serviceTypeIds.Distinct().ToList();
        if (selectedServiceIds.Count == 0)
        {
            return new List<TimeSpan>();
        }

        var serviceTypes = await _db.ServiceTypes
            .Where(s => selectedServiceIds.Contains(s.Id) && s.IsActive)
            .ToListAsync();

        if (serviceTypes.Count != selectedServiceIds.Count)
        {
            return new List<TimeSpan>();
        }

        var workingHour = await _db.WorkingHours
            .FirstOrDefaultAsync(w => w.DayOfWeek == date.DayOfWeek);

        if (workingHour is null || !workingHour.IsOpen)
        {
            return new List<TimeSpan>();
        }

        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = date.ToDateTime(TimeOnly.MaxValue);

        var existingAppointments = await _db.Appointments
            .Where(a => a.Status != AppointmentStatus.Cancelled &&
                        a.StartTime >= dayStart && a.StartTime <= dayEnd)
            .Select(a => new { a.StartTime, a.EndTime })
            .ToListAsync();

        var duration = TimeSpan.FromMinutes(CalculateCombinedDurationMinutes(serviceTypes));
        var step = TimeSpan.FromMinutes(_slotStepMinutes);
        var now = DateTime.Now;

        var slots = new List<TimeSpan>();
        var candidate = date.ToDateTime(TimeOnly.FromTimeSpan(workingHour.StartTime));
        var closing = date.ToDateTime(TimeOnly.FromTimeSpan(workingHour.EndTime));

        while (candidate + duration <= closing)
        {
            var candidateEnd = candidate + duration;

            var isPast = date == GetMinBookingDate() && candidate <= now;
            var overlaps = existingAppointments.Any(a => candidate < a.EndTime && a.StartTime < candidateEnd);

            if (!isPast && !overlaps)
            {
                slots.Add(candidate.TimeOfDay);
            }

            candidate = candidate.Add(step);
        }

        return slots;
    }

    public Task<AppointmentResult> CreateAppointmentAsync(int customerId, int serviceTypeId, DateTime startTime) =>
        CreateAppointmentAsync(customerId, new[] { serviceTypeId }, startTime);

    public async Task<AppointmentResult> CreateAppointmentAsync(int customerId, IReadOnlyCollection<int> serviceTypeIds, DateTime startTime)
    {
        var date = DateOnly.FromDateTime(startTime);
        var selectedServiceIds = serviceTypeIds.Distinct().ToList();

        var availableSlots = await GetAvailableSlotsAsync(date, selectedServiceIds);
        if (!availableSlots.Contains(startTime.TimeOfDay))
        {
            return new AppointmentResult(false, "Seçilen saat artık uygun değil, lütfen başka bir saat seçin.");
        }

        var serviceTypes = await _db.ServiceTypes
            .Where(s => selectedServiceIds.Contains(s.Id) && s.IsActive)
            .ToListAsync();

        if (serviceTypes.Count == 0 || serviceTypes.Count != selectedServiceIds.Count)
        {
            return new AppointmentResult(false, "Seçilen hizmetler bulunamadı.");
        }

        var customer = await _db.Customers.FindAsync(customerId);
        if (customer is null)
        {
            return new AppointmentResult(false, "Müşteri bulunamadı.");
        }

        var primaryService = serviceTypes
            .OrderByDescending(s => s.DurationMinutes)
            .ThenBy(s => s.SortOrder)
            .First();
        var durationMinutes = CalculateCombinedDurationMinutes(serviceTypes);

        var appointment = new Appointment
        {
            CustomerId = customerId,
            ServiceTypeId = primaryService.Id,
            StartTime = startTime,
            EndTime = startTime.AddMinutes(durationMinutes),
            Status = AppointmentStatus.Confirmed,
            CreatedAt = DateTime.UtcNow
        };

        _db.Appointments.Add(appointment);
        await _db.SaveChangesAsync();

        _db.AppointmentServiceItems.AddRange(serviceTypes.Select(s => new AppointmentServiceItem
        {
            AppointmentId = appointment.Id,
            ServiceTypeId = s.Id
        }));
        await _db.SaveChangesAsync();

        appointment.ServiceItems = serviceTypes
            .OrderBy(s => s.SortOrder)
            .Select(s => new AppointmentServiceItem { AppointmentId = appointment.Id, ServiceTypeId = s.Id, ServiceType = s })
            .ToList();

        var serviceNames = string.Join(" + ", serviceTypes.OrderBy(s => s.SortOrder).Select(s => s.Name));
        await _smsService.SendAsync(customer.PhoneNumber,
            $"Randevunuz alindi: {startTime:dd.MM.yyyy HH:mm} - {serviceNames}. Kuaför Arif sizi bekliyor!");

        return new AppointmentResult(true, null, appointment);
    }

    public async Task<List<Appointment>> GetCustomerAppointmentsAsync(int customerId)
    {
        return await _db.Appointments
            .Include(a => a.ServiceType)
            .Include(a => a.ServiceItems)
                .ThenInclude(i => i.ServiceType)
            .Where(a => a.CustomerId == customerId)
            .OrderByDescending(a => a.StartTime)
            .ToListAsync();
    }

    public async Task<List<Appointment>> GetAllAppointmentsAsync(DateOnly? date = null, AppointmentStatus? status = null)
    {
        var query = _db.Appointments
            .Include(a => a.Customer)
            .Include(a => a.ServiceType)
            .Include(a => a.ServiceItems)
                .ThenInclude(i => i.ServiceType)
            .AsQueryable();

        if (date.HasValue)
        {
            var start = date.Value.ToDateTime(TimeOnly.MinValue);
            var end = date.Value.ToDateTime(TimeOnly.MaxValue);
            query = query.Where(a => a.StartTime >= start && a.StartTime <= end);
        }

        if (status.HasValue)
        {
            query = query.Where(a => a.Status == status.Value);
        }

        return await query.OrderBy(a => a.StartTime).ToListAsync();
    }

    public async Task<AppointmentResult> CancelAppointmentAsync(int appointmentId, string cancelledBy, string? reason)
    {
        var appointment = await _db.Appointments
            .Include(a => a.Customer)
            .Include(a => a.ServiceType)
            .Include(a => a.ServiceItems)
                .ThenInclude(i => i.ServiceType)
            .FirstOrDefaultAsync(a => a.Id == appointmentId);

        if (appointment is null)
        {
            return new AppointmentResult(false, "Randevu bulunamadı.");
        }

        if (appointment.Status == AppointmentStatus.Cancelled)
        {
            return new AppointmentResult(false, "Randevu zaten iptal edilmiş.");
        }

        appointment.Status = AppointmentStatus.Cancelled;
        appointment.CancelledAt = DateTime.UtcNow;
        appointment.CancelReason = reason;
        appointment.CancelledBy = cancelledBy;

        await _db.SaveChangesAsync();

        if (appointment.Customer is not null)
        {
            var serviceNames = appointment.ServiceItems.Count > 0
                ? string.Join(" + ", appointment.ServiceItems
                    .Where(i => i.ServiceType is not null)
                    .OrderBy(i => i.ServiceType!.SortOrder)
                    .Select(i => i.ServiceType!.Name))
                : appointment.ServiceType?.Name;

            await _smsService.SendAsync(appointment.Customer.PhoneNumber,
                $"Randevunuz iptal edilmistir: {appointment.StartTime:dd.MM.yyyy HH:mm} - {serviceNames}. " +
                "Bilgi için Kuaför Arif ile iletişime geçebilirsiniz.");
        }

        return new AppointmentResult(true, null, appointment);
    }

    private static int CalculateCombinedDurationMinutes(IReadOnlyCollection<ServiceType> serviceTypes)
    {
        if (serviceTypes.Count == 0)
        {
            return 0;
        }

        var orderedDurations = serviceTypes
            .Select(s => s.DurationMinutes)
            .OrderByDescending(d => d)
            .ToList();

        var total = orderedDurations[0] + orderedDurations.Skip(1).Sum(d => d / 2.0);
        return (int)(Math.Ceiling(total / 5.0) * 5);
    }
}
