using System.Collections.Frozen;
using System.Data.Common;
using System.Data;
using System.Globalization;
using System.Text;
using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Common.Extensions;
using Enterprise.Gpt.Repository.Sql.DbContexts;
using Enterprise.Gpt.Repository;
using Enterprise.Gpt.Service.Chunking;
using Enterprise.Gpt.Service.Settings;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Enterprise.Gpt.Service.Documents.Retrieval.Models;

/// <summary>
/// A chunk with its fused rank-fusion score and the evidence each pass contributed.
/// </summary>
/// <param name="Key">Which chunk this is.</param>
/// <param name="Score">The Reciprocal Rank Fusion score across both passes; higher is better.</param>
/// <param name="Distance">The cosine distance, or <see langword="null"/> when only the keyword pass found it.</param>
/// <param name="MatchCount">The number of query terms found in the chunk; zero when only the vector pass found it.</param>
internal readonly record struct ScoredChunk(ChunkKey Key, double Score, double? Distance, int MatchCount);
