using Microsoft.EntityFrameworkCore;

namespace CommandLineMcp.Data;

public sealed class CommandLineMcpDbContext(DbContextOptions<CommandLineMcpDbContext> options)
    : DbContext(options);
