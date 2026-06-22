using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IRequestRepository, InMemoryRequestRepository>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/api", () => Results.Ok(new
{
    service = "Сервис заявок на бухгалтерские справки",
    version = "1.0",
    endpoints = new[]
    {
        "/api/certificate-types",
        "/api/requests",
        "/api/accounting/requests"
    }
}));

app.MapGet("/api/certificate-types", () =>
    Results.Ok(Enum.GetValues<CertificateType>()
        .Select(CertificateTypeResponse.FromType)));

var employeeGroup = app.MapGroup("/api/requests");

employeeGroup.MapPost("/", (
    CreateRequestDto request,
    IRequestRepository repository,
    TimeProvider timeProvider) =>
{
    var errors = ValidateCreateRequest(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var duplicate = repository.FindDuplicate(request.EmployeeNumber, request.CertificateType, request.CopyCount, request.Reason);
    if (duplicate is not null)
    {
        return Results.Conflict(new ProblemDetails
        {
            Title = "Дубликат заявки",
            Detail = $"Активная заявка с такими же данными уже существует: {duplicate.Id}",
            Status = StatusCodes.Status409Conflict
        });
    }

    var item = CertificateRequest.Create(request, timeProvider.GetUtcNow());
    repository.Add(item);

    return Results.Created($"/api/requests/{item.Id}", CertificateRequestResponse.FromEntity(item));
});

employeeGroup.MapGet("/{id:guid}", (Guid id, IRequestRepository repository) =>
{
    var item = repository.GetById(id);
    return item is null ? Results.NotFound() : Results.Ok(CertificateRequestResponse.FromEntity(item));
});

employeeGroup.MapGet("/employee/{employeeNumber}", (string employeeNumber, IRequestRepository repository) =>
{
    if (string.IsNullOrWhiteSpace(employeeNumber))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(employeeNumber)] = ["Табельный номер обязателен."]
        });
    }

    var items = repository.GetByEmployee(employeeNumber)
        .Select(CertificateRequestResponse.FromEntity)
        .ToArray();

    return Results.Ok(items);
});

var accountingGroup = app.MapGroup("/api/accounting/requests");

accountingGroup.MapGet("/", (IRequestRepository repository, string? status) =>
{
    RequestStatus? parsedStatus = null;

    if (!string.IsNullOrWhiteSpace(status))
    {
        if (!Enum.TryParse<RequestStatus>(status, true, out var parsed))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(status)] = ["Неизвестный статус."]
            });
        }

        parsedStatus = parsed;
    }

    var items = repository.GetQueue(parsedStatus)
        .Select(CertificateRequestResponse.FromEntity)
        .ToArray();

    return Results.Ok(items);
});

accountingGroup.MapPatch("/{id:guid}/status", (
    Guid id,
    UpdateStatusDto request,
    IRequestRepository repository,
    TimeProvider timeProvider) =>
{
    var errors = ValidateStatusUpdate(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var item = repository.GetById(id);
    if (item is null)
    {
        return Results.NotFound();
    }

    item.UpdateStatus(request.Status, request.ChangedBy.Trim(), request.Comment?.Trim(), timeProvider.GetUtcNow());
    repository.Update(item);

    return Results.Ok(CertificateRequestResponse.FromEntity(item));
});

app.Run();

static Dictionary<string, string[]> ValidateCreateRequest(CreateRequestDto request)
{
    var errors = new Dictionary<string, string[]>();

    if (string.IsNullOrWhiteSpace(request.EmployeeNumber))
    {
        errors[nameof(request.EmployeeNumber)] = ["Табельный номер обязателен."];
    }

    if (string.IsNullOrWhiteSpace(request.EmployeeName))
    {
        errors[nameof(request.EmployeeName)] = ["ФИО сотрудника обязательно."];
    }

    if (request.CopyCount is < 1 or > 10)
    {
        errors[nameof(request.CopyCount)] = ["Количество экземпляров должно быть от 1 до 10."];
    }

    if (string.IsNullOrWhiteSpace(request.Reason))
    {
        errors[nameof(request.Reason)] = ["Причина запроса обязательна."];
    }

    if (request.CertificateType == CertificateType.Custom && string.IsNullOrWhiteSpace(request.CustomCertificateName))
    {
        errors[nameof(request.CustomCertificateName)] = ["Для произвольной справки нужно указать название."];
    }

    return errors;
}

static Dictionary<string, string[]> ValidateStatusUpdate(UpdateStatusDto request)
{
    var errors = new Dictionary<string, string[]>();

    if (string.IsNullOrWhiteSpace(request.ChangedBy))
    {
        errors[nameof(request.ChangedBy)] = ["Нужно указать, кто изменил статус."];
    }

    return errors;
}

enum CertificateType
{
    TwoNdfl,
    EmploymentAndTenure,
    AverageEarnings,
    Custom
}

enum RequestStatus
{
    Created,
    InProgress,
    Completed,
    Rejected
}

sealed class CertificateRequest
{
    private readonly List<StatusHistoryEntry> _history = [];

    private CertificateRequest(
        Guid id,
        string employeeNumber,
        string employeeName,
        CertificateType certificateType,
        int copyCount,
        string reason,
        string? customCertificateName,
        DateTimeOffset createdAt)
    {
        Id = id;
        EmployeeNumber = employeeNumber;
        EmployeeName = employeeName;
        CertificateType = certificateType;
        CopyCount = copyCount;
        Reason = reason;
        CustomCertificateName = customCertificateName;
        Status = RequestStatus.Created;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }
    public string EmployeeNumber { get; }
    public string EmployeeName { get; }
    public CertificateType CertificateType { get; }
    public int CopyCount { get; }
    public string Reason { get; }
    public string? CustomCertificateName { get; }
    public RequestStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<StatusHistoryEntry> History => _history;

    public static CertificateRequest Create(CreateRequestDto request, DateTimeOffset now)
    {
        var item = new CertificateRequest(
            Guid.NewGuid(),
            request.EmployeeNumber.Trim(),
            request.EmployeeName.Trim(),
            request.CertificateType,
            request.CopyCount,
            request.Reason.Trim(),
            request.CertificateType == CertificateType.Custom ? request.CustomCertificateName?.Trim() : null,
            now);

        item._history.Add(new StatusHistoryEntry(item.Status, now, "система", "Заявка создана"));
        return item;
    }

    public void UpdateStatus(RequestStatus status, string changedBy, string? comment, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
        _history.Add(new StatusHistoryEntry(status, now, changedBy, comment));
    }
}

sealed record StatusHistoryEntry(RequestStatus Status, DateTimeOffset ChangedAt, string ChangedBy, string? Comment);

sealed record CreateRequestDto(
    [property: JsonPropertyName("табельныйНомер")] string EmployeeNumber,
    [property: JsonPropertyName("фиоСотрудника")] string EmployeeName,
    [property: JsonPropertyName("типСправки")] CertificateType CertificateType,
    [property: JsonPropertyName("количествоЭкземпляров")] int CopyCount,
    [property: JsonPropertyName("причина")] string Reason,
    [property: JsonPropertyName("названиеПроизвольнойСправки")] string? CustomCertificateName);

sealed record UpdateStatusDto(
    [property: JsonPropertyName("статус")] RequestStatus Status,
    [property: JsonPropertyName("ктоИзменил")] string ChangedBy,
    [property: JsonPropertyName("комментарий")] string? Comment);

sealed record CertificateTypeResponse(
    [property: JsonPropertyName("код")] CertificateType Code,
    [property: JsonPropertyName("название")] string Name)
{
    public static CertificateTypeResponse FromType(CertificateType type) => new(type, type switch
    {
        CertificateType.TwoNdfl => "2-НДФЛ",
        CertificateType.EmploymentAndTenure => "О месте работы и стаже",
        CertificateType.AverageEarnings => "О среднем заработке",
        CertificateType.Custom => "Произвольная справка",
        _ => type.ToString()
    });
}

sealed record CertificateRequestResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("табельныйНомер")] string EmployeeNumber,
    [property: JsonPropertyName("фиоСотрудника")] string EmployeeName,
    [property: JsonPropertyName("типСправки")] CertificateType CertificateType,
    [property: JsonPropertyName("названиеТипаСправки")] string CertificateTypeName,
    [property: JsonPropertyName("количествоЭкземпляров")] int CopyCount,
    [property: JsonPropertyName("причина")] string Reason,
    [property: JsonPropertyName("названиеПроизвольнойСправки")] string? CustomCertificateName,
    [property: JsonPropertyName("статус")] RequestStatus Status,
    [property: JsonPropertyName("создана")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("обновлена")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("история")] IReadOnlyList<StatusHistoryResponse> History)
{
    public static CertificateRequestResponse FromEntity(CertificateRequest item) => new(
        item.Id,
        item.EmployeeNumber,
        item.EmployeeName,
        item.CertificateType,
        CertificateTypeResponse.FromType(item.CertificateType).Name,
        item.CopyCount,
        item.Reason,
        item.CustomCertificateName,
        item.Status,
        item.CreatedAt,
        item.UpdatedAt,
        item.History.Select(x => new StatusHistoryResponse(x.Status, x.ChangedAt, x.ChangedBy, x.Comment)).ToArray());
}

sealed record StatusHistoryResponse(
    [property: JsonPropertyName("статус")] RequestStatus Status,
    [property: JsonPropertyName("изменено")] DateTimeOffset ChangedAt,
    [property: JsonPropertyName("ктоИзменил")] string ChangedBy,
    [property: JsonPropertyName("комментарий")] string? Comment);

interface IRequestRepository
{
    void Add(CertificateRequest item);
    CertificateRequest? GetById(Guid id);
    IReadOnlyCollection<CertificateRequest> GetByEmployee(string employeeNumber);
    IReadOnlyCollection<CertificateRequest> GetQueue(RequestStatus? status);
    CertificateRequest? FindDuplicate(string employeeNumber, CertificateType certificateType, int copyCount, string reason);
    void Update(CertificateRequest item);
}

sealed class InMemoryRequestRepository : IRequestRepository
{
    private readonly ConcurrentDictionary<Guid, CertificateRequest> _storage = new();

    public void Add(CertificateRequest item) => _storage[item.Id] = item;

    public CertificateRequest? GetById(Guid id) => _storage.TryGetValue(id, out var item) ? item : null;

    public IReadOnlyCollection<CertificateRequest> GetByEmployee(string employeeNumber) =>
        _storage.Values
            .Where(item => string.Equals(item.EmployeeNumber, employeeNumber.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.CreatedAt)
            .ToArray();

    public IReadOnlyCollection<CertificateRequest> GetQueue(RequestStatus? status)
    {
        var query = _storage.Values.AsEnumerable();
        if (status.HasValue)
        {
            query = query.Where(item => item.Status == status.Value);
        }

        return query
            .OrderBy(item => item.Status)
            .ThenBy(item => item.CreatedAt)
            .ToArray();
    }

    public CertificateRequest? FindDuplicate(string employeeNumber, CertificateType certificateType, int copyCount, string reason) =>
        _storage.Values.FirstOrDefault(item =>
            string.Equals(item.EmployeeNumber, employeeNumber.Trim(), StringComparison.OrdinalIgnoreCase) &&
            item.CertificateType == certificateType &&
            item.CopyCount == copyCount &&
            string.Equals(item.Reason, reason.Trim(), StringComparison.OrdinalIgnoreCase) &&
            item.Status is not RequestStatus.Completed and not RequestStatus.Rejected);

    public void Update(CertificateRequest item) => _storage[item.Id] = item;
}
