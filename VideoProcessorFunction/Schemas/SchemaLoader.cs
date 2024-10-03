namespace VideoProcessorFunction.Schemas
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;

    public interface ISchemaLoader
    {
        string LoadSchema(string schemaName);
    }

    public class SchemaLoader : ISchemaLoader
    {
        private readonly string _basePath;

        /// <summary>
        /// Fetch schema from a local filesystem
        /// </summary>
        /// <param name="basePath">The path containing schemas</param>
        /// <exception cref="ArgumentNullException">Guard clause triggered</exception>
        public SchemaLoader(string basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath))
                throw new ArgumentNullException("basePath cannot be null or empty");
            _basePath = basePath;
        }

        /// <summary>
        /// Load schema from the filesystem
        /// </summary>
        /// <param name="schemaName">The file name containing the schema to load</param>
        /// <returns>The json schema document as a string</returns>
        /// <exception cref="InvalidOperationException">Detailed explanation of why schema didn't load</exception>
        public string LoadSchema(string schemaName)
        {
            string schema = File.ReadAllText($"{_basePath}/{schemaName}");
            if (string.IsNullOrWhiteSpace(schema))
                throw new InvalidOperationException($"Schema {schemaName} not found at {_basePath}");
            return schema;
        }
    }
}