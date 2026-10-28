using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Switchboard.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ActivityEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EntityType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ActorKind = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    ClientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ChangesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivityEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdmImports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CsvText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MappingJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdateExisting = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TotalRows = table.Column<int>(type: "int", nullable: false),
                    CreatedCount = table.Column<int>(type: "int", nullable: false),
                    UpdatedCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    ErrorCount = table.Column<int>(type: "int", nullable: false),
                    ErrorsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdmImports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiIdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Route = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AccessHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StatusCode = table.Column<int>(type: "int", nullable: true),
                    ContentType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Location = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ETag = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ResponseBody = table.Column<byte[]>(type: "varbinary(max)", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiIdempotencyRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiWebhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    ProtectedSecret = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SecretHint = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Events = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastDeliveryAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastDeliverySucceeded = table.Column<bool>(type: "bit", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiWebhooks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FileName = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsInline = table.Column<bool>(type: "bit", nullable: false),
                    UploadedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActorKind = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    OnBehalfOfUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ClientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Ip = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ObjectType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ObjectId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequestId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PrevHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Hash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Sequence);
                });

            migrationBuilder.CreateTable(
                name: "AutAlerts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutEventLineage",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CascadeRootId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutEventLineage", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "AutQueuedEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EventType = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutQueuedEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WorkflowName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "int", nullable: true),
                    RunKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TriggerKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TriggerSummary = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Anchor = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CascadeRootId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CascadeDepth = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Message = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AutTicketClocks",
                columns: table => new
                {
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StatusSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutTicketClocks", x => x.TicketId);
                });

            migrationBuilder.CreateTable(
                name: "CatCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Icon = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatSharedFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FieldType = table.Column<int>(type: "int", nullable: false),
                    HelpText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Options = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatSharedFields", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomFieldDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EntityType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    RequesterLabel = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FieldType = table.Column<int>(type: "int", nullable: false),
                    OptionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequiredForAgents = table.Column<bool>(type: "bit", nullable: false),
                    RequiredOnResolve = table.Column<bool>(type: "bit", nullable: false),
                    RequiredForRequesters = table.Column<bool>(type: "bit", nullable: false),
                    VisibleToRequesters = table.Column<bool>(type: "bit", nullable: false),
                    EditableByRequesters = table.Column<bool>(type: "bit", nullable: false),
                    HelpText = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DefaultValue = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Section = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFieldDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Departments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    HeadUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PrimeUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Domains = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CustomFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Departments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DisplayIdCounters",
                columns: table => new
                {
                    Family = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    NextValue = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DisplayIdCounters", x => x.Family);
                });

            migrationBuilder.CreateTable(
                name: "KbAnnouncements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BodyText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Audience = table.Column<int>(type: "int", nullable: false),
                    ActiveFrom = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActiveTo = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsPublished = table.Column<bool>(type: "bit", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbAnnouncements", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KbCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Locations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AddressLine1 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    AddressLine2 = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    City = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    State = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PostalCode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Country = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContactName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContactEmail = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ContactPhone = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Locations_Locations_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Locations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "McpCallRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClientId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ClientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Tool = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ActionClass = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ResultCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LatencyMs = table.Column<int>(type: "int", nullable: false),
                    PendingActionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ViaApiKey = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpCallRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    MetadataUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    ClientUri = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    RedirectUris = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsConfidential = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RegisteredFromIp = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MetadataFetchedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BlockedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    BlockedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpClients", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ClientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Resource = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ConfigurationSlug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RedirectHost = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedReason = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RevokedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpGrants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpIdempotencyRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    GrantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConfigurationSlug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Tool = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ParameterHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpIdempotencyRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpOAuthApplications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ClientId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ClientSecret = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClientType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ConsentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayNames = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    JsonWebKeySet = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Permissions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PostLogoutRedirectUris = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RedirectUris = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Requirements = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Settings = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOAuthApplications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpOAuthScopes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Descriptions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DisplayNames = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Resources = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOAuthScopes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpPendingActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GrantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ClientId = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    ClientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    SessionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ConfigurationSlug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Tool = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ToolTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ActionClass = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ArgumentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ParameterHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Method = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RequiresReauthentication = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResultCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ResultText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpPendingActions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpServerConfigurations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Slug = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    DisabledTools = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AllowedRoleIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AllowRequesters = table.Column<bool>(type: "bit", nullable: false),
                    AllowedClientIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpServerConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpSigningKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Use = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ProtectedKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpSigningKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MsgAddressRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Pattern = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MsgAddressRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MsgBouncedAddresses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Address = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Count = table.Column<int>(type: "int", nullable: false),
                    FirstBouncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastBouncedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MsgBouncedAddresses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MsgDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Recipient = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TemplateKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    MessageId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    IsAutomatic = table.Column<bool>(type: "bit", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MsgDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MsgInboundMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MailboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Source = table.Column<int>(type: "int", nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    FromName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    OutcomeDetail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SuppressAutoResponses = table.Column<bool>(type: "bit", nullable: false),
                    AuthenticationFailed = table.Column<bool>(type: "bit", nullable: false),
                    TraceJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RawStorageKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MsgInboundMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MsgMailboxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DefaultGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TicketType = table.Column<int>(type: "int", nullable: false),
                    Connection = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    ImapHost = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ImapPort = table.Column<int>(type: "int", nullable: false),
                    ImapSecurity = table.Column<int>(type: "int", nullable: false),
                    ImapUsername = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    ImapPasswordProtected = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ImapFolder = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "int", nullable: false),
                    WebhookSecretProtected = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    TrustedAuthServId = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    TrustUnauthenticated = table.Column<bool>(type: "bit", nullable: false),
                    AllowEmailCommands = table.Column<bool>(type: "bit", nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MsgMailboxes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MsgMessageMap",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MailboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Direction = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MsgMessageMap", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MsgNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Link = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Reference = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MsgNotifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MsgOutboundSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    Port = table.Column<int>(type: "int", nullable: false),
                    Security = table.Column<int>(type: "int", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    PasswordProtected = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    FromName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    EnvelopeFrom = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    DkimDomain = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    DkimSelector = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DkimKeyProtected = table.Column<string>(type: "nvarchar(max)", maxLength: 12000, nullable: true),
                    CaptureAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    NonProductionAllowlist = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MsgOutboundSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MsgPreferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Email = table.Column<bool>(type: "bit", nullable: false),
                    InApp = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MsgPreferences", x => new { x.UserId, x.EventKey });
                });

            migrationBuilder.CreateTable(
                name: "MsgTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MsgTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    LogoUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    BaseUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DateFormat = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    FirstDayOfWeek = table.Column<int>(type: "int", nullable: false),
                    SetupCompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxHandlerReceipts",
                columns: table => new
                {
                    MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HandlerName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxHandlerReceipts", x => new { x.MessageId, x.HandlerName });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CascadeDepth = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    LastError = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RequesterGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsRuleBased = table.Column<bool>(type: "bit", nullable: false),
                    RuleJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequesterGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    Privileges = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SdBulkJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Total = table.Column<int>(type: "int", nullable: false),
                    Succeeded = table.Column<int>(type: "int", nullable: false),
                    Failed = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SdBulkJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ValueJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TicketSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Icon = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketSources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Icon = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Template = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false),
                    IsSetupMode = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApiWebhookDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WebhookId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastStatusCode = table.Column<int>(type: "int", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ResponseSnippet = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiWebhookDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiWebhookDeliveries_ApiWebhooks_WebhookId",
                        column: x => x.WebhookId,
                        principalTable: "ApiWebhooks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutRunSteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    InputJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OutputJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    IsExternal = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutRunSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutRunSteps_AutRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "AutRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KbAnnouncementTargets",
                columns: table => new
                {
                    AnnouncementId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbAnnouncementTargets", x => new { x.AnnouncementId, x.Kind, x.TargetId });
                    table.ForeignKey(
                        name: "FK_KbAnnouncementTargets_KbAnnouncements_AnnouncementId",
                        column: x => x.AnnouncementId,
                        principalTable: "KbAnnouncements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KbFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Visibility = table.Column<int>(type: "int", nullable: false),
                    RequiresApproval = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KbFolders_KbCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "KbCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KbFolders_KbFolders_ParentId",
                        column: x => x.ParentId,
                        principalTable: "KbFolders",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    JobTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Mobile = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Language = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    AvatarUrl = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Signature = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Background = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LocationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ManagerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    IsAgent = table.Column<bool>(type: "bit", nullable: false),
                    IsOrgAdmin = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsVip = table.Column<bool>(type: "bit", nullable: false),
                    IsForgotten = table.Column<bool>(type: "bit", nullable: false),
                    Availability = table.Column<int>(type: "int", nullable: false),
                    OutOfOfficeMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    OutOfOfficeUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Skills = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CustomFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeactivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Users_Locations_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Locations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Users_Users_ManagerId",
                        column: x => x.ManagerId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "McpOAuthAuthorizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Scopes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOAuthAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOAuthAuthorizations_McpOAuthApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "McpOAuthApplications",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "SdBulkJobItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    JobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayId = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SdBulkJobItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SdBulkJobItems_SdBulkJobs_JobId",
                        column: x => x.JobId,
                        principalTable: "SdBulkJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    AssignmentMode = table.Column<int>(type: "int", nullable: false),
                    MaxOpenPerAgent = table.Column<int>(type: "int", nullable: true),
                    UnassignedEscalationMinutes = table.Column<int>(type: "int", nullable: true),
                    EscalateToUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BusinessCalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastAssignedUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentGroups_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AutClosureRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TicketType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    FieldKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutClosureRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutClosureRules_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AutScenarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Visibility = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ActionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutScenarios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutScenarios_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AutSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    TemplateJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Frequency = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DaysOfWeek = table.Column<int>(type: "int", nullable: false),
                    DayOfMonth = table.Column<int>(type: "int", nullable: true),
                    TimeOfDay = table.Column<TimeOnly>(type: "time", nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    NextRunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastTicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutSchedules_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AutWorkflows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Module = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    DraftJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedVersion = table.Column<int>(type: "int", nullable: true),
                    PublishedJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PublishedTrigger = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "int", nullable: false),
                    AutoDisabledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DisabledReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastRunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRunStatus = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: true),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutWorkflows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutWorkflows_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SdTicketTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DescriptionHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ItemCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Priority = table.Column<int>(type: "int", nullable: true),
                    Urgency = table.Column<int>(type: "int", nullable: true),
                    Impact = table.Column<int>(type: "int", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Tags = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SdTicketTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SdTicketTemplates_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SlaCalendars",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Is24x7 = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaCalendars", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaCalendars_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketCategories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketCategories_TicketCategories_ParentId",
                        column: x => x.ParentId,
                        principalTable: "TicketCategories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TicketCategories_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TicketStatuses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequesterLabel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Category = table.Column<int>(type: "int", nullable: false),
                    SlaTimerOn = table.Column<bool>(type: "bit", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    IsSystem = table.Column<bool>(type: "bit", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketStatuses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketStatuses_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "KbFolderAudiences",
                columns: table => new
                {
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbFolderAudiences", x => new { x.FolderId, x.Kind, x.TargetId });
                    table.ForeignKey(
                        name: "FK_KbFolderAudiences_KbFolders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "KbFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdmUserMerges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SurvivorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MergedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MergedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdmUserMerges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdmUserMerges_Users_SourceUserId",
                        column: x => x.SourceUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AdmUserMerges_Users_SurvivorUserId",
                        column: x => x.SurvivorUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Prefix = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Scopes = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ApiKeys_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AprRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubjectType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    RequesterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CurrentLevel = table.Column<int>(type: "int", nullable: true),
                    WaitingSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ConflictUserIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AllowSelfApproval = table.Column<bool>(type: "bit", nullable: false),
                    DecisionCount = table.Column<int>(type: "int", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancelledById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    LastRemindedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReminderCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AprRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AprRequests_Users_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "InsSavedReports",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Folder = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Path = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Query = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Sharing = table.Column<int>(type: "int", nullable: false),
                    SharedGroupIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsSavedReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InsSavedReports_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KbArticles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReviewBy = table.Column<DateOnly>(type: "date", nullable: true),
                    Tags = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Keywords = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SearchTerms = table.Column<string>(type: "nvarchar(1200)", maxLength: 1200, nullable: false),
                    ExternalUrl = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    SeoTitle = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SeoDescription = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    PublishedVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LatestVersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ViewCount = table.Column<int>(type: "int", nullable: false),
                    SourceTicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbArticles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KbArticles_KbFolders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "KbFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_KbArticles_Users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_KbArticles_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "KbFolderApprovers",
                columns: table => new
                {
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbFolderApprovers", x => new { x.FolderId, x.UserId });
                    table.ForeignKey(
                        name: "FK_KbFolderApprovers_KbFolders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "KbFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KbFolderApprovers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RequesterGroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequesterGroupMembers", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_RequesterGroupMembers_RequesterGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "RequesterGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RequesterGroupMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SdSavedViews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Query = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    Sort = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    Columns = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsShared = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SdSavedViews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SdSavedViews_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserClaims_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserDelegations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DelegateUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserDelegations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserDelegations_Users_DelegateUserId",
                        column: x => x.DelegateUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_UserDelegations_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserEmails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserEmails_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_UserLogins_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_UserTokens_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RoleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketScope = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceMemberships_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspaceMemberships_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_WorkspaceMemberships_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "McpOAuthTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AuthorizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConcurrencyToken = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    CreationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpirationDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Payload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RedemptionDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReferenceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    Type = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpOAuthTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_McpOAuthTokens_McpOAuthApplications_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "McpOAuthApplications",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_McpOAuthTokens_McpOAuthAuthorizations_AuthorizationId",
                        column: x => x.AuthorizationId,
                        principalTable: "McpOAuthAuthorizations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AgentGroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsObserver = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentGroupMembers", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_AgentGroupMembers_AgentGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "AgentGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentGroupMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CatItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ShortDescription = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: true),
                    Icon = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    CostMinor = table.Column<long>(type: "bigint", nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    DeliveryDays = table.Column<int>(type: "int", nullable: true),
                    MaxQuantity = table.Column<int>(type: "int", nullable: false),
                    AllowRequestedFor = table.Column<bool>(type: "bit", nullable: false),
                    VisibleToGroupIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    VisibleToDepartmentIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FulfillmentGroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ApprovalConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PostSubmitMessage = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    BundleItemIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatItems_AgentGroups_FulfillmentGroupId",
                        column: x => x.FulfillmentGroupId,
                        principalTable: "AgentGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SdCannedResponseFolders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Visibility = table.Column<int>(type: "int", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SdCannedResponseFolders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SdCannedResponseFolders_AgentGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "AgentGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SdCannedResponseFolders_Users_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutScheduleOccurrences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurrenceAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutScheduleOccurrences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutScheduleOccurrences_AutSchedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "AutSchedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutWorkflowVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutWorkflowVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutWorkflowVersions_AutWorkflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "AutWorkflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SlaHolidays",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaHolidays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaHolidays_SlaCalendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "SlaCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SlaPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConditionsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TargetsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RemindersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EscalationsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaPolicies_SlaCalendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "SlaCalendars",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SlaPolicies_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SlaWorkingHours",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Day = table.Column<int>(type: "int", nullable: false),
                    StartMinute = table.Column<int>(type: "int", nullable: false),
                    EndMinute = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaWorkingHours", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaWorkingHours_SlaCalendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "SlaCalendars",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tickets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    DescriptionHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DescriptionText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RequesterId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestedForId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CcEmails = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    StatusId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    Urgency = table.Column<int>(type: "int", nullable: false),
                    Impact = table.Column<int>(type: "int", nullable: false),
                    SourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ItemCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AgentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DepartmentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FirstResponseDueBy = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DueBy = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FirstRespondedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlannedStartAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlannedEndAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlannedEffortMinutes = table.Column<int>(type: "int", nullable: true),
                    SlaPolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AwaitingAgentSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastRequesterActivityAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastAgentActivityAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsMajorIncident = table.Column<bool>(type: "bit", nullable: false),
                    IsSpam = table.Column<bool>(type: "bit", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PurgedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    MergedIntoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CustomFieldsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tickets_AgentGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "AgentGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Departments_DepartmentId",
                        column: x => x.DepartmentId,
                        principalTable: "Departments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tickets_TicketCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "TicketCategories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tickets_TicketCategories_ItemCategoryId",
                        column: x => x.ItemCategoryId,
                        principalTable: "TicketCategories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tickets_TicketCategories_SubCategoryId",
                        column: x => x.SubCategoryId,
                        principalTable: "TicketCategories",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tickets_TicketSources_SourceId",
                        column: x => x.SourceId,
                        principalTable: "TicketSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_TicketStatuses_StatusId",
                        column: x => x.StatusId,
                        principalTable: "TicketStatuses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Tickets_Tickets_MergedIntoId",
                        column: x => x.MergedIntoId,
                        principalTable: "Tickets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tickets_Tickets_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Tickets",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tickets_Users_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tickets_Users_RequestedForId",
                        column: x => x.RequestedForId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tickets_Users_RequesterId",
                        column: x => x.RequesterId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Tickets_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AprLevels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Rule = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AprLevels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AprLevels_AprRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "AprRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AprLinkTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LevelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OnBehalfOfId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AprLinkTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AprLinkTokens_AprRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "AprRequests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InsReportFavorites",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReportId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsReportFavorites", x => new { x.UserId, x.ReportId });
                    table.ForeignKey(
                        name: "FK_InsReportFavorites_InsSavedReports_ReportId",
                        column: x => x.ReportId,
                        principalTable: "InsSavedReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InsReportFavorites_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "KbArticleFeedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VisitorKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Helpful = table.Column<bool>(type: "bit", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbArticleFeedback", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KbArticleFeedback_KbArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "KbArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KbArticleRelations",
                columns: table => new
                {
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RelatedArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbArticleRelations", x => new { x.ArticleId, x.RelatedArticleId });
                    table.ForeignKey(
                        name: "FK_KbArticleRelations_KbArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "KbArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_KbArticleRelations_KbArticles_RelatedArticleId",
                        column: x => x.RelatedArticleId,
                        principalTable: "KbArticles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "KbArticleReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SubmittedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SubmitNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Decision = table.Column<int>(type: "int", nullable: false),
                    DecidedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DecisionComment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbArticleReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KbArticleReviews_KbArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "KbArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KbArticleVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Number = table.Column<int>(type: "int", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BodyText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Excerpt = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    ChangeNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PublishedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RestoredFromNumber = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbArticleVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KbArticleVersions_KbArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "KbArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "KbDeflections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArticleId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ViewedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KbDeflections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KbDeflections_KbArticles_ArticleId",
                        column: x => x.ArticleId,
                        principalTable: "KbArticles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CatItemCategories",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatItemCategories", x => new { x.ItemId, x.CategoryId });
                    table.ForeignKey(
                        name: "FK_CatItemCategories_CatCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "CatCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CatItemCategories_CatItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "CatItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CatItemFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SharedFieldId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Key = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Label = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FieldType = table.Column<int>(type: "int", nullable: false),
                    Required = table.Column<bool>(type: "bit", nullable: false),
                    HelpText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Options = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatItemFields", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatItemFields_CatItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "CatItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CatItemFields_CatSharedFields_SharedFieldId",
                        column: x => x.SharedFieldId,
                        principalTable: "CatSharedFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SdCannedResponses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FolderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SdCannedResponses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SdCannedResponses_SdCannedResponseFolders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "SdCannedResponseFolders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CatRequestedItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ItemName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Icon = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    AnswersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Stage = table.Column<int>(type: "int", nullable: false),
                    UnitCostMinor = table.Column<long>(type: "bigint", nullable: true),
                    CostMinor = table.Column<long>(type: "bigint", nullable: true),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    RequestedForId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    StageChangedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatRequestedItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatRequestedItems_CatItems_ItemId",
                        column: x => x.ItemId,
                        principalTable: "CatItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CatRequestedItems_CatRequestedItems_ParentId",
                        column: x => x.ParentId,
                        principalTable: "CatRequestedItems",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CatRequestedItems_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SlaEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Cycle = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Target = table.Column<int>(type: "int", nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FiredAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    NotifiedUserIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SlaEvents_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SlaMonitorCursors",
                columns: table => new
                {
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Cycle = table.Column<int>(type: "int", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FirstResponseDueBy = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DueBy = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextResponseDueBy = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextResponseSeq = table.Column<int>(type: "int", nullable: false),
                    NextCheckAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaMonitorCursors", x => x.TicketId);
                    table.ForeignKey(
                        name: "FK_SlaMonitorCursors_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SlaTicketStates",
                columns: table => new
                {
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PolicyName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    SelectionReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    SelectedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    TargetPriority = table.Column<int>(type: "int", nullable: true),
                    FirstResponseMinutes = table.Column<int>(type: "int", nullable: true),
                    NextResponseMinutes = table.Column<int>(type: "int", nullable: true),
                    ResolutionMinutes = table.Column<int>(type: "int", nullable: true),
                    BusinessHours = table.Column<bool>(type: "bit", nullable: false),
                    CalendarId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Cycle = table.Column<int>(type: "int", nullable: false),
                    CycleStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    PausedSince = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PausesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FirstResponseTargetAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ResolutionTargetAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FirstResponseOverridden = table.Column<bool>(type: "bit", nullable: false),
                    ResolutionOverridden = table.Column<bool>(type: "bit", nullable: false),
                    OverrideReason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    OverriddenById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OverriddenAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextResponseStartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextResponseDueBy = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextResponseSeq = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SlaTicketStates", x => x.TicketId);
                    table.ForeignKey(
                        name: "FK_SlaTicketStates_SlaCalendars_CalendarId",
                        column: x => x.CalendarId,
                        principalTable: "SlaCalendars",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SlaTicketStates_SlaPolicies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "SlaPolicies",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SlaTicketStates_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketConversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    IsPrivate = table.Column<bool>(type: "bit", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    FromEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    To = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Cc = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Bcc = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BodyHtml = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BodyText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    EmailMessageId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EditedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketConversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketConversations_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TicketConversations_Users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TicketLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LinkType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketLinks_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketShares",
                columns: table => new
                {
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SharedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketShares", x => new { x.TicketId, x.UserId });
                    table.ForeignKey(
                        name: "FK_TicketShares_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TicketShares_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "TicketTags",
                columns: table => new
                {
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tag = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketTags", x => new { x.TicketId, x.Tag });
                    table.ForeignKey(
                        name: "FK_TicketTags_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TicketTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TicketId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AssigneeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NotifyBeforeMinutes = table.Column<int>(type: "int", nullable: true),
                    PlannedStartAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlannedEndAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    PlannedEffortMinutes = table.Column<int>(type: "int", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Order = table.Column<int>(type: "int", nullable: false),
                    CreatedById = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TicketTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TicketTasks_AgentGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "AgentGroups",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_TicketTasks_Tickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "Tickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TicketTasks_Users_AssigneeId",
                        column: x => x.AssigneeId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AprApprovers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LevelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AprApprovers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AprApprovers_AprLevels_LevelId",
                        column: x => x.LevelId,
                        principalTable: "AprLevels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AprApprovers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "AprDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequestId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LevelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ApproverId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DecidedById = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OnBehalfOfId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Verdict = table.Column<int>(type: "int", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    ClientName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AprDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AprDecisions_AprLevels_LevelId",
                        column: x => x.LevelId,
                        principalTable: "AprLevels",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AprDecisions_AprRequests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "AprRequests",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AprDecisions_Users_ApproverId",
                        column: x => x.ApproverId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AprDecisions_Users_DecidedById",
                        column: x => x.DecidedById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityEntries_SubjectType_SubjectId_CreatedAt",
                table: "ActivityEntries",
                columns: new[] { "SubjectType", "SubjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AdmImports_CreatedAt",
                table: "AdmImports",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AdmUserMerges_SourceUserId",
                table: "AdmUserMerges",
                column: "SourceUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdmUserMerges_SurvivorUserId",
                table: "AdmUserMerges",
                column: "SurvivorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentGroupMembers_UserId",
                table: "AgentGroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentGroups_WorkspaceId_Name",
                table: "AgentGroups",
                columns: new[] { "WorkspaceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiIdempotencyRecords_ExpiresAt",
                table: "ApiIdempotencyRecords",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ApiIdempotencyRecords_UserId_Key_Route",
                table: "ApiIdempotencyRecords",
                columns: new[] { "UserId", "Key", "Route" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_KeyHash",
                table: "ApiKeys",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_UserId",
                table: "ApiKeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ApiWebhookDeliveries_Status_NextAttemptAt",
                table: "ApiWebhookDeliveries",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiWebhookDeliveries_WebhookId_CreatedAt",
                table: "ApiWebhookDeliveries",
                columns: new[] { "WebhookId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApiWebhookDeliveries_WebhookId_EventId",
                table: "ApiWebhookDeliveries",
                columns: new[] { "WebhookId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AprApprovers_LevelId_UserId",
                table: "AprApprovers",
                columns: new[] { "LevelId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AprApprovers_UserId",
                table: "AprApprovers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AprDecisions_ApproverId",
                table: "AprDecisions",
                column: "ApproverId");

            migrationBuilder.CreateIndex(
                name: "IX_AprDecisions_DecidedById",
                table: "AprDecisions",
                column: "DecidedById");

            migrationBuilder.CreateIndex(
                name: "IX_AprDecisions_LevelId_ApproverId",
                table: "AprDecisions",
                columns: new[] { "LevelId", "ApproverId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AprDecisions_RequestId",
                table: "AprDecisions",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_AprLevels_RequestId_Order",
                table: "AprLevels",
                columns: new[] { "RequestId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AprLinkTokens_RequestId",
                table: "AprLinkTokens",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_AprLinkTokens_TokenHash",
                table: "AprLinkTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AprRequests_RequesterId",
                table: "AprRequests",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_AprRequests_Status_CurrentLevel",
                table: "AprRequests",
                columns: new[] { "Status", "CurrentLevel" });

            migrationBuilder.CreateIndex(
                name: "IX_AprRequests_SubjectType_SubjectId",
                table: "AprRequests",
                columns: new[] { "SubjectType", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_OwnerType_OwnerId",
                table: "Attachments",
                columns: new[] { "OwnerType", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_Id",
                table: "AuditEntries",
                column: "Id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_ObjectType_ObjectId",
                table: "AuditEntries",
                columns: new[] { "ObjectType", "ObjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_OccurredAt",
                table: "AuditEntries",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_AutAlerts_Key",
                table: "AutAlerts",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutClosureRules_WorkspaceId",
                table: "AutClosureRules",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_AutEventLineage_CreatedAt",
                table: "AutEventLineage",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AutQueuedEvents_EventId",
                table: "AutQueuedEvents",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutQueuedEvents_WorkspaceId_OccurredAt",
                table: "AutQueuedEvents",
                columns: new[] { "WorkspaceId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutRuns_RunKey",
                table: "AutRuns",
                column: "RunKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutRuns_StartedAt",
                table: "AutRuns",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_AutRuns_TicketId_StartedAt",
                table: "AutRuns",
                columns: new[] { "TicketId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutRuns_WorkflowId_StartedAt",
                table: "AutRuns",
                columns: new[] { "WorkflowId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutRuns_WorkflowId_TicketId_Anchor",
                table: "AutRuns",
                columns: new[] { "WorkflowId", "TicketId", "Anchor" },
                filter: "[Anchor] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AutRuns_WorkflowId_TicketId_CascadeRootId",
                table: "AutRuns",
                columns: new[] { "WorkflowId", "TicketId", "CascadeRootId" },
                unique: true,
                filter: "[WorkflowId] IS NOT NULL AND [TicketId] IS NOT NULL AND [Status] <> 'Blocked'");

            migrationBuilder.CreateIndex(
                name: "IX_AutRunSteps_RunId_Order",
                table: "AutRunSteps",
                columns: new[] { "RunId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_AutRunSteps_Status_CreatedAt",
                table: "AutRunSteps",
                columns: new[] { "Status", "CreatedAt" },
                filter: "[IsExternal] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_AutScenarios_OwnerUserId",
                table: "AutScenarios",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AutScenarios_WorkspaceId_Visibility",
                table: "AutScenarios",
                columns: new[] { "WorkspaceId", "Visibility" });

            migrationBuilder.CreateIndex(
                name: "IX_AutScheduleOccurrences_ScheduleId_OccurrenceAt",
                table: "AutScheduleOccurrences",
                columns: new[] { "ScheduleId", "OccurrenceAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutSchedules_IsEnabled_NextRunAt",
                table: "AutSchedules",
                columns: new[] { "IsEnabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AutSchedules_WorkspaceId",
                table: "AutSchedules",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_AutTicketClocks_StatusSince",
                table: "AutTicketClocks",
                column: "StatusSince");

            migrationBuilder.CreateIndex(
                name: "IX_AutWorkflows_WorkspaceId_Module_Order",
                table: "AutWorkflows",
                columns: new[] { "WorkspaceId", "Module", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_AutWorkflows_WorkspaceId_Name",
                table: "AutWorkflows",
                columns: new[] { "WorkspaceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AutWorkflows_WorkspaceId_PublishedTrigger_IsEnabled",
                table: "AutWorkflows",
                columns: new[] { "WorkspaceId", "PublishedTrigger", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_AutWorkflowVersions_WorkflowId_Number",
                table: "AutWorkflowVersions",
                columns: new[] { "WorkflowId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatCategories_Name",
                table: "CatCategories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatItemCategories_CategoryId",
                table: "CatItemCategories",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_CatItemFields_ItemId_Key",
                table: "CatItemFields",
                columns: new[] { "ItemId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatItemFields_SharedFieldId",
                table: "CatItemFields",
                column: "SharedFieldId");

            migrationBuilder.CreateIndex(
                name: "IX_CatItems_FulfillmentGroupId",
                table: "CatItems",
                column: "FulfillmentGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_CatItems_Name",
                table: "CatItems",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_CatItems_State",
                table: "CatItems",
                column: "State");

            migrationBuilder.CreateIndex(
                name: "IX_CatRequestedItems_ItemId_CreatedAt",
                table: "CatRequestedItems",
                columns: new[] { "ItemId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CatRequestedItems_ParentId",
                table: "CatRequestedItems",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_CatRequestedItems_TicketId",
                table: "CatRequestedItems",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_CatSharedFields_Key",
                table: "CatSharedFields",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldDefinitions_EntityType_WorkspaceId_Key",
                table: "CustomFieldDefinitions",
                columns: new[] { "EntityType", "WorkspaceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Departments_Name",
                table: "Departments",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InsReportFavorites_ReportId",
                table: "InsReportFavorites",
                column: "ReportId");

            migrationBuilder.CreateIndex(
                name: "IX_InsSavedReports_OwnerId_DeletedAt",
                table: "InsSavedReports",
                columns: new[] { "OwnerId", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InsSavedReports_Sharing_DeletedAt",
                table: "InsSavedReports",
                columns: new[] { "Sharing", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KbAnnouncements_IsPublished_ActiveFrom_ActiveTo",
                table: "KbAnnouncements",
                columns: new[] { "IsPublished", "ActiveFrom", "ActiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_KbArticleFeedback_ArticleId_UserId",
                table: "KbArticleFeedback",
                columns: new[] { "ArticleId", "UserId" },
                unique: true,
                filter: "[UserId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_KbArticleFeedback_ArticleId_VisitorKey",
                table: "KbArticleFeedback",
                columns: new[] { "ArticleId", "VisitorKey" },
                unique: true,
                filter: "[VisitorKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_KbArticleRelations_RelatedArticleId",
                table: "KbArticleRelations",
                column: "RelatedArticleId");

            migrationBuilder.CreateIndex(
                name: "IX_KbArticleReviews_ArticleId_Decision",
                table: "KbArticleReviews",
                columns: new[] { "ArticleId", "Decision" });

            migrationBuilder.CreateIndex(
                name: "IX_KbArticles_AuthorId",
                table: "KbArticles",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_KbArticles_FolderId_Status",
                table: "KbArticles",
                columns: new[] { "FolderId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_KbArticles_Number",
                table: "KbArticles",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KbArticles_OwnerId",
                table: "KbArticles",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_KbArticles_WorkspaceId_DeletedAt_Status",
                table: "KbArticles",
                columns: new[] { "WorkspaceId", "DeletedAt", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_KbArticleVersions_ArticleId_Number",
                table: "KbArticleVersions",
                columns: new[] { "ArticleId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KbArticleVersions_OnePublished",
                table: "KbArticleVersions",
                column: "ArticleId",
                unique: true,
                filter: "[State] = 3");

            migrationBuilder.CreateIndex(
                name: "IX_KbCategories_WorkspaceId_Order",
                table: "KbCategories",
                columns: new[] { "WorkspaceId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_KbDeflections_ArticleId_TicketId",
                table: "KbDeflections",
                columns: new[] { "ArticleId", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_KbDeflections_UserId_ViewedAt",
                table: "KbDeflections",
                columns: new[] { "UserId", "ViewedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_KbFolderApprovers_UserId",
                table: "KbFolderApprovers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_KbFolders_CategoryId_Order",
                table: "KbFolders",
                columns: new[] { "CategoryId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_KbFolders_ParentId",
                table: "KbFolders",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Locations_ParentId",
                table: "Locations",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_McpCallRecords_At",
                table: "McpCallRecords",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_McpCallRecords_GrantId_At",
                table: "McpCallRecords",
                columns: new[] { "GrantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_McpCallRecords_UserId_At",
                table: "McpCallRecords",
                columns: new[] { "UserId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_McpClients_ClientId",
                table: "McpClients",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpGrants_ClientId",
                table: "McpGrants",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_McpGrants_UserId_RevokedAt",
                table: "McpGrants",
                columns: new[] { "UserId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_McpIdempotencyRecords_CreatedAt",
                table: "McpIdempotencyRecords",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_McpIdempotencyRecords_UserId_ClientId_GrantId_ConfigurationSlug_Tool_Key",
                table: "McpIdempotencyRecords",
                columns: new[] { "UserId", "ClientId", "GrantId", "ConfigurationSlug", "Tool", "Key" },
                unique: true,
                filter: "[GrantId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_McpOAuthApplications_ClientId",
                table: "McpOAuthApplications",
                column: "ClientId",
                unique: true,
                filter: "[ClientId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_McpOAuthAuthorizations_ApplicationId_Status_Subject_Type",
                table: "McpOAuthAuthorizations",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOAuthScopes_Name",
                table: "McpOAuthScopes",
                column: "Name",
                unique: true,
                filter: "[Name] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_McpOAuthTokens_ApplicationId_Status_Subject_Type",
                table: "McpOAuthTokens",
                columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_McpOAuthTokens_AuthorizationId",
                table: "McpOAuthTokens",
                column: "AuthorizationId");

            migrationBuilder.CreateIndex(
                name: "IX_McpOAuthTokens_ReferenceId",
                table: "McpOAuthTokens",
                column: "ReferenceId",
                unique: true,
                filter: "[ReferenceId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_McpPendingActions_UserId_Status_CreatedAt",
                table: "McpPendingActions",
                columns: new[] { "UserId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_McpServerConfigurations_Slug",
                table: "McpServerConfigurations",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MsgAddressRules_Pattern",
                table: "MsgAddressRules",
                column: "Pattern",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MsgBouncedAddresses_Address",
                table: "MsgBouncedAddresses",
                column: "Address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MsgDeliveries_EventId_Recipient_Channel",
                table: "MsgDeliveries",
                columns: new[] { "EventId", "Recipient", "Channel" },
                unique: true,
                filter: "[EventId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MsgDeliveries_MessageId",
                table: "MsgDeliveries",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MsgDeliveries_Recipient_CreatedAt",
                table: "MsgDeliveries",
                columns: new[] { "Recipient", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MsgDeliveries_TicketId_CreatedAt",
                table: "MsgDeliveries",
                columns: new[] { "TicketId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MsgInboundMessages_MessageId",
                table: "MsgInboundMessages",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_MsgInboundMessages_ReceivedAt",
                table: "MsgInboundMessages",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_MsgInboundMessages_TicketId",
                table: "MsgInboundMessages",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_MsgMailboxes_Address",
                table: "MsgMailboxes",
                column: "Address",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MsgMailboxes_WorkspaceId",
                table: "MsgMailboxes",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_MsgMessageMap_MessageId",
                table: "MsgMessageMap",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MsgMessageMap_TicketId_CreatedAt",
                table: "MsgMessageMap",
                columns: new[] { "TicketId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MsgNotifications_UserId_ReadAt_CreatedAt",
                table: "MsgNotifications",
                columns: new[] { "UserId", "ReadAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MsgTemplates_WorkspaceId_EventKey",
                table: "MsgTemplates",
                columns: new[] { "WorkspaceId", "EventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_ProcessedAt_NextAttemptAt",
                table: "OutboxMessages",
                columns: new[] { "ProcessedAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RequesterGroupMembers_UserId",
                table: "RequesterGroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RequesterGroups_Name",
                table: "RequesterGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Roles_Key",
                table: "Roles",
                column: "Key",
                unique: true,
                filter: "[Key] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SdBulkJobItems_JobId_Order",
                table: "SdBulkJobItems",
                columns: new[] { "JobId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_SdBulkJobs_CreatedById",
                table: "SdBulkJobs",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_SdBulkJobs_Status",
                table: "SdBulkJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SdCannedResponseFolders_GroupId",
                table: "SdCannedResponseFolders",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_SdCannedResponseFolders_OwnerId",
                table: "SdCannedResponseFolders",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_SdCannedResponseFolders_Visibility_OwnerId",
                table: "SdCannedResponseFolders",
                columns: new[] { "Visibility", "OwnerId" });

            migrationBuilder.CreateIndex(
                name: "IX_SdCannedResponses_FolderId",
                table: "SdCannedResponses",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_SdCannedResponses_Title",
                table: "SdCannedResponses",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_SdSavedViews_IsShared",
                table: "SdSavedViews",
                column: "IsShared");

            migrationBuilder.CreateIndex(
                name: "IX_SdSavedViews_OwnerId_Name",
                table: "SdSavedViews",
                columns: new[] { "OwnerId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_SdTicketTemplates_WorkspaceId_Name",
                table: "SdTicketTemplates",
                columns: new[] { "WorkspaceId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Settings_WorkspaceId_Key",
                table: "Settings",
                columns: new[] { "WorkspaceId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlaCalendars_WorkspaceId_DeletedAt",
                table: "SlaCalendars",
                columns: new[] { "WorkspaceId", "DeletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaEvents_TicketId_Cycle_Kind_Target_Level_Sequence",
                table: "SlaEvents",
                columns: new[] { "TicketId", "Cycle", "Kind", "Target", "Level", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlaEvents_WorkspaceId_FiredAt",
                table: "SlaEvents",
                columns: new[] { "WorkspaceId", "FiredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaHolidays_CalendarId_Date",
                table: "SlaHolidays",
                columns: new[] { "CalendarId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SlaMonitorCursors_NextCheckAt",
                table: "SlaMonitorCursors",
                column: "NextCheckAt");

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_CalendarId",
                table: "SlaPolicies",
                column: "CalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaPolicies_WorkspaceId_DeletedAt_Order",
                table: "SlaPolicies",
                columns: new[] { "WorkspaceId", "DeletedAt", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaTicketStates_CalendarId",
                table: "SlaTicketStates",
                column: "CalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaTicketStates_NextResponseDueBy",
                table: "SlaTicketStates",
                column: "NextResponseDueBy");

            migrationBuilder.CreateIndex(
                name: "IX_SlaTicketStates_PolicyId",
                table: "SlaTicketStates",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_SlaTicketStates_WorkspaceId_PausedSince",
                table: "SlaTicketStates",
                columns: new[] { "WorkspaceId", "PausedSince" });

            migrationBuilder.CreateIndex(
                name: "IX_SlaWorkingHours_CalendarId",
                table: "SlaWorkingHours",
                column: "CalendarId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketCategories_ParentId",
                table: "TicketCategories",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketCategories_WorkspaceId_ParentId_Name",
                table: "TicketCategories",
                columns: new[] { "WorkspaceId", "ParentId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketConversations_AuthorId",
                table: "TicketConversations",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketConversations_EmailMessageId",
                table: "TicketConversations",
                column: "EmailMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketConversations_TicketId_CreatedAt",
                table: "TicketConversations",
                columns: new[] { "TicketId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketLinks_TargetType_TargetId",
                table: "TicketLinks",
                columns: new[] { "TargetType", "TargetId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketLinks_TicketId_TargetType_TargetId_LinkType",
                table: "TicketLinks",
                columns: new[] { "TicketId", "TargetType", "TargetId", "LinkType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_AgentId",
                table: "Tickets",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CategoryId",
                table: "Tickets",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_CreatedAt",
                table: "Tickets",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_DepartmentId",
                table: "Tickets",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_DueBy",
                table: "Tickets",
                column: "DueBy");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_GroupId",
                table: "Tickets",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ItemCategoryId",
                table: "Tickets",
                column: "ItemCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_MergedIntoId",
                table: "Tickets",
                column: "MergedIntoId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_Number",
                table: "Tickets",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_ParentId",
                table: "Tickets",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_RequestedForId",
                table: "Tickets",
                column: "RequestedForId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_RequesterId",
                table: "Tickets",
                column: "RequesterId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_SourceId",
                table: "Tickets",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_StatusId",
                table: "Tickets",
                column: "StatusId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_SubCategoryId",
                table: "Tickets",
                column: "SubCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_UpdatedAt",
                table: "Tickets",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Tickets_WorkspaceId_StatusId",
                table: "Tickets",
                columns: new[] { "WorkspaceId", "StatusId" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketShares_UserId",
                table: "TicketShares",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketSources_Key",
                table: "TicketSources",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketStatuses_WorkspaceId_Name",
                table: "TicketStatuses",
                columns: new[] { "WorkspaceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TicketTags_Tag",
                table: "TicketTags",
                column: "Tag");

            migrationBuilder.CreateIndex(
                name: "IX_TicketTasks_AssigneeId_Status",
                table: "TicketTasks",
                columns: new[] { "AssigneeId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_TicketTasks_GroupId",
                table: "TicketTasks",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_TicketTasks_TicketId",
                table: "TicketTasks",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_UserClaims_UserId",
                table: "UserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserDelegations_DelegateUserId",
                table: "UserDelegations",
                column: "DelegateUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserDelegations_UserId_StartsAt_EndsAt",
                table: "UserDelegations",
                columns: new[] { "UserId", "StartsAt", "EndsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserEmails_NormalizedEmail",
                table: "UserEmails",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserEmails_UserId",
                table: "UserEmails",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserLogins_UserId",
                table: "UserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "Users",
                column: "NormalizedEmail",
                unique: true,
                filter: "[NormalizedEmail] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_DepartmentId",
                table: "Users",
                column: "DepartmentId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_DisplayName",
                table: "Users",
                column: "DisplayName");

            migrationBuilder.CreateIndex(
                name: "IX_Users_IsAgent_IsActive",
                table: "Users",
                columns: new[] { "IsAgent", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_LocationId",
                table: "Users",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_ManagerId",
                table: "Users",
                column: "ManagerId");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "Users",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMemberships_RoleId",
                table: "WorkspaceMemberships",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMemberships_UserId_WorkspaceId",
                table: "WorkspaceMemberships",
                columns: new[] { "UserId", "WorkspaceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMemberships_WorkspaceId",
                table: "WorkspaceMemberships",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_Key",
                table: "Workspaces",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivityEntries");

            migrationBuilder.DropTable(
                name: "AdmImports");

            migrationBuilder.DropTable(
                name: "AdmUserMerges");

            migrationBuilder.DropTable(
                name: "AgentGroupMembers");

            migrationBuilder.DropTable(
                name: "ApiIdempotencyRecords");

            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "ApiWebhookDeliveries");

            migrationBuilder.DropTable(
                name: "AprApprovers");

            migrationBuilder.DropTable(
                name: "AprDecisions");

            migrationBuilder.DropTable(
                name: "AprLinkTokens");

            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "AutAlerts");

            migrationBuilder.DropTable(
                name: "AutClosureRules");

            migrationBuilder.DropTable(
                name: "AutEventLineage");

            migrationBuilder.DropTable(
                name: "AutQueuedEvents");

            migrationBuilder.DropTable(
                name: "AutRunSteps");

            migrationBuilder.DropTable(
                name: "AutScenarios");

            migrationBuilder.DropTable(
                name: "AutScheduleOccurrences");

            migrationBuilder.DropTable(
                name: "AutTicketClocks");

            migrationBuilder.DropTable(
                name: "AutWorkflowVersions");

            migrationBuilder.DropTable(
                name: "CatItemCategories");

            migrationBuilder.DropTable(
                name: "CatItemFields");

            migrationBuilder.DropTable(
                name: "CatRequestedItems");

            migrationBuilder.DropTable(
                name: "CustomFieldDefinitions");

            migrationBuilder.DropTable(
                name: "DisplayIdCounters");

            migrationBuilder.DropTable(
                name: "InsReportFavorites");

            migrationBuilder.DropTable(
                name: "KbAnnouncementTargets");

            migrationBuilder.DropTable(
                name: "KbArticleFeedback");

            migrationBuilder.DropTable(
                name: "KbArticleRelations");

            migrationBuilder.DropTable(
                name: "KbArticleReviews");

            migrationBuilder.DropTable(
                name: "KbArticleVersions");

            migrationBuilder.DropTable(
                name: "KbDeflections");

            migrationBuilder.DropTable(
                name: "KbFolderApprovers");

            migrationBuilder.DropTable(
                name: "KbFolderAudiences");

            migrationBuilder.DropTable(
                name: "McpCallRecords");

            migrationBuilder.DropTable(
                name: "McpClients");

            migrationBuilder.DropTable(
                name: "McpGrants");

            migrationBuilder.DropTable(
                name: "McpIdempotencyRecords");

            migrationBuilder.DropTable(
                name: "McpOAuthScopes");

            migrationBuilder.DropTable(
                name: "McpOAuthTokens");

            migrationBuilder.DropTable(
                name: "McpPendingActions");

            migrationBuilder.DropTable(
                name: "McpServerConfigurations");

            migrationBuilder.DropTable(
                name: "McpSigningKeys");

            migrationBuilder.DropTable(
                name: "MsgAddressRules");

            migrationBuilder.DropTable(
                name: "MsgBouncedAddresses");

            migrationBuilder.DropTable(
                name: "MsgDeliveries");

            migrationBuilder.DropTable(
                name: "MsgInboundMessages");

            migrationBuilder.DropTable(
                name: "MsgMailboxes");

            migrationBuilder.DropTable(
                name: "MsgMessageMap");

            migrationBuilder.DropTable(
                name: "MsgNotifications");

            migrationBuilder.DropTable(
                name: "MsgOutboundSettings");

            migrationBuilder.DropTable(
                name: "MsgPreferences");

            migrationBuilder.DropTable(
                name: "MsgTemplates");

            migrationBuilder.DropTable(
                name: "Organizations");

            migrationBuilder.DropTable(
                name: "OutboxHandlerReceipts");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "RequesterGroupMembers");

            migrationBuilder.DropTable(
                name: "SdBulkJobItems");

            migrationBuilder.DropTable(
                name: "SdCannedResponses");

            migrationBuilder.DropTable(
                name: "SdSavedViews");

            migrationBuilder.DropTable(
                name: "SdTicketTemplates");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "SlaEvents");

            migrationBuilder.DropTable(
                name: "SlaHolidays");

            migrationBuilder.DropTable(
                name: "SlaMonitorCursors");

            migrationBuilder.DropTable(
                name: "SlaTicketStates");

            migrationBuilder.DropTable(
                name: "SlaWorkingHours");

            migrationBuilder.DropTable(
                name: "TicketConversations");

            migrationBuilder.DropTable(
                name: "TicketLinks");

            migrationBuilder.DropTable(
                name: "TicketShares");

            migrationBuilder.DropTable(
                name: "TicketTags");

            migrationBuilder.DropTable(
                name: "TicketTasks");

            migrationBuilder.DropTable(
                name: "UserClaims");

            migrationBuilder.DropTable(
                name: "UserDelegations");

            migrationBuilder.DropTable(
                name: "UserEmails");

            migrationBuilder.DropTable(
                name: "UserLogins");

            migrationBuilder.DropTable(
                name: "UserTokens");

            migrationBuilder.DropTable(
                name: "WorkspaceMemberships");

            migrationBuilder.DropTable(
                name: "ApiWebhooks");

            migrationBuilder.DropTable(
                name: "AprLevels");

            migrationBuilder.DropTable(
                name: "AutRuns");

            migrationBuilder.DropTable(
                name: "AutSchedules");

            migrationBuilder.DropTable(
                name: "AutWorkflows");

            migrationBuilder.DropTable(
                name: "CatCategories");

            migrationBuilder.DropTable(
                name: "CatSharedFields");

            migrationBuilder.DropTable(
                name: "CatItems");

            migrationBuilder.DropTable(
                name: "InsSavedReports");

            migrationBuilder.DropTable(
                name: "KbAnnouncements");

            migrationBuilder.DropTable(
                name: "KbArticles");

            migrationBuilder.DropTable(
                name: "McpOAuthAuthorizations");

            migrationBuilder.DropTable(
                name: "RequesterGroups");

            migrationBuilder.DropTable(
                name: "SdBulkJobs");

            migrationBuilder.DropTable(
                name: "SdCannedResponseFolders");

            migrationBuilder.DropTable(
                name: "SlaPolicies");

            migrationBuilder.DropTable(
                name: "Tickets");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "AprRequests");

            migrationBuilder.DropTable(
                name: "KbFolders");

            migrationBuilder.DropTable(
                name: "McpOAuthApplications");

            migrationBuilder.DropTable(
                name: "SlaCalendars");

            migrationBuilder.DropTable(
                name: "AgentGroups");

            migrationBuilder.DropTable(
                name: "TicketCategories");

            migrationBuilder.DropTable(
                name: "TicketSources");

            migrationBuilder.DropTable(
                name: "TicketStatuses");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "KbCategories");

            migrationBuilder.DropTable(
                name: "Workspaces");

            migrationBuilder.DropTable(
                name: "Departments");

            migrationBuilder.DropTable(
                name: "Locations");
        }
    }
}
