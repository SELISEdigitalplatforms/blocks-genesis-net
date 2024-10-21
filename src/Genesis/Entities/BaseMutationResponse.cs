using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Blocks.Genesis.Entities
{
    public class BaseMutationResponse
    {
        public IDictionary<string, string>? Errors { get; set; }
        public bool Success { get; set; }
        public string? ItemId { get; set; }
    }
}
