using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CosmosDbGraphUploader.Console.Csv
{
    public class CsvProfile
    {
        public virtual char FieldSeparator { get; set; }
        public char TextQualifier { get; set; }
        public virtual string RecordSeparator { get; set; }

        protected CsvProfile()
        {
        }

        public CsvProfile(char fieldSeparator, char textQualifier)
            : this(fieldSeparator, textQualifier, Environment.NewLine)
        {
        }

        public CsvProfile(char fieldSeparator, char textQualifier, string recordSeparator)
            : this()
        {
            FieldSeparator = fieldSeparator;
            TextQualifier = textQualifier;
            RecordSeparator = recordSeparator;
        }

        public CsvProfile(char fieldSeparator, string recordSeparator)
            : this(fieldSeparator, '\"', recordSeparator)
        {
        }

        public static CsvProfile CommaNewLineDoubleQuote
        {
            get
            {
                return new CsvProfile(',', '\"');
            }
        }

        public static CsvProfile SemiColumnDoubleQuote
        {
            get
            {
                return new CsvProfile(';', '\"');
            }
        }

    }

}
