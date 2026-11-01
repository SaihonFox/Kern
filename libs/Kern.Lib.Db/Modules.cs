using Kern.Binding.Attributes;

namespace Kern.Lib.Db;

[KernModule("kern.db")]
public static class DbModule
{
	[KernMember]
	public static string Version() => "0.1.0";
}

[KernModule("kern.db.sqlite")]
public static class SqliteModule
{
	[KernMember]
	public static string Version() => "0.1.0";
}

[KernModule("kern.db.postgres")]
public static class PostgresModule
{
	[KernMember]
	public static string Version() => "0.1.0";
}

[KernModule("kern.db.mysql")]
public static class MysqlModule
{
	[KernMember]
	public static string Version() => "0.1.0";
}
