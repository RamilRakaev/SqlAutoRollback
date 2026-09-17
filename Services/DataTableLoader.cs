using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlAutoRollback.Services;

public static class DataTableLoader
{
    public static DataTable Load(SqlDataReader reader, bool convertBinaryToHex = true)
    {
        var table = new DataTable();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = $"Column{i + 1}";
            }

            var unique = name;
            var suffix = 1;
            while (!usedNames.Add(unique))
            {
                unique = $"{name}{suffix++}";
            }

            var type = reader.GetFieldType(i) ?? typeof(object);
            if (convertBinaryToHex && type == typeof(byte[]))
            {
                type = typeof(string);
            }

            table.Columns.Add(unique, Nullable.GetUnderlyingType(type) ?? type);
        }

        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                if (reader.IsDBNull(i))
                {
                    values[i] = DBNull.Value;
                    continue;
                }

                var value = reader.GetValue(i);
                values[i] = convertBinaryToHex && value is byte[] bytes
                    ? Convert.ToHexString(bytes)
                    : value;
            }

            table.Rows.Add(values);
        }

        return table;
    }
}
