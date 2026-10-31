using Kern.Binding.Attributes;

// Внешняя библиотека БД: карта C#-namespace → Kern-модули. Подключается kernc из папки libs (см. docs/LIBRARIES.md).
[assembly: KernModule("kern.db.sqlite", "Kern.Lib.Db.Sqlite")]
[assembly: KernModule("kern.db.postgres", "Kern.Lib.Db.Postgres")]
[assembly: KernModule("kern.db.mysql", "Kern.Lib.Db.Mysql")]
[assembly: KernModule("kern.db", "Kern.Lib.Db.Orm")]
