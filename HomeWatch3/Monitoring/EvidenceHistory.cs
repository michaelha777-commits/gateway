using HomeWatch3.Data;
using Microsoft.EntityFrameworkCore;

namespace HomeWatch3.Monitoring;

public sealed class EvidenceSearch
{
    public DateOnly? From {get;set;}
    public DateOnly? Through {get;set;}
    public long? DeviceId {get;set;}
    public string? Source {get;set;}
    public string? Category {get;set;}
    public string? Search {get;set;}
    public DateTimeOffset? Before {get;set;}
    public long? BeforeId {get;set;}
    public bool? IncludeReverse {get;set;}
}

internal static class EvidenceHistory
{
    public static void MapEvidenceHistory(this WebApplication app)
    {
        app.MapGet("/api/history/evidence", async ([AsParameters] EvidenceSearch filters, HomeWatchDb db,
            IgnoredDeviceStore devices, IgnoredDomainStore domains, EvidenceDomainPolicy policy, CancellationToken ct) =>
        {
            var rows=await Query(filters,db,devices,domains,policy).Take(101).ToListAsync(ct);
            var more=rows.Count>100;
            if(more)rows.RemoveAt(100);
            var last=rows.LastOrDefault();
            return Results.Ok(new {rows,next=more && last!=null?new {before=DateTime.SpecifyKind(last.TimestampUtc,DateTimeKind.Utc),beforeId=last.Id}:null});
        });
        app.MapGet("/api/history/evidence/export", async (string format,[AsParameters] EvidenceSearch filters, HomeWatchDb db,
            IgnoredDeviceStore devices, IgnoredDomainStore domains, EvidenceDomainPolicy policy,HttpResponse response,CancellationToken ct) =>
        {
            var rows=Query(filters,db,devices,domains,policy).AsAsyncEnumerable();
            if(format=="json"){
                response.Headers.ContentDisposition="attachment; filename=homewatch-evidence.json";
                await response.WriteAsJsonAsync(rows,ct);return;
            }
            if(format!="csv"){response.StatusCode=400;return;}
            response.ContentType="text/csv; charset=utf-8";
            response.Headers.ContentDisposition="attachment; filename=homewatch-evidence.csv";
            await response.WriteAsync("Id,TimeUtc,DeviceId,Source,Domain,Application,Category,Blocked,BytesDown,BytesUp\r\n",ct);
            await foreach(var row in rows.WithCancellation(ct)){
                var fields=new[]{row.Id.ToString(),DateTime.SpecifyKind(row.TimestampUtc,DateTimeKind.Utc).ToString("O"),
                    row.DeviceId?.ToString(),row.Source,row.Domain,row.Application,row.Category,row.Blocked.ToString(),row.BytesDown?.ToString(),row.BytesUp?.ToString()};
                await response.WriteAsync(string.Join(",",fields.Select(Csv))+"\r\n",ct);
            }
        });
    }

    private static string Csv(string? value)
    {
        var field=value??"";
        if(field.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || field.StartsWith('\t') || field.StartsWith('\r'))field="'"+field;
        return "\""+field.Replace("\"","\"\"")+"\"";
    }

    private static IOrderedQueryable<TrafficEvent> Query(EvidenceSearch f,HomeWatchDb db,IgnoredDeviceStore devices,
        IgnoredDomainStore domains,EvidenceDomainPolicy policy)
    {
        var query=db.TrafficEvents.AsNoTracking();
        var ignored=devices.GetIds().ToArray();
        query=query.Where(x=>!x.DeviceId.HasValue || !ignored.Contains(x.DeviceId.Value));
        foreach(var root in domains.GetDomains().Concat(policy.GetTrustedDomains()).Distinct())
            query=query.Where(x=>x.Domain==null || (x.Domain!=root && !x.Domain.EndsWith("."+root)));
        if(f.DeviceId.HasValue){var members=DeviceIdentity.Members(db,f.DeviceId.Value);query=query.Where(x=>x.DeviceId.HasValue && members.Contains(x.DeviceId.Value));}
        if(!string.IsNullOrWhiteSpace(f.Source))query=query.Where(x=>x.Source==f.Source);
        if(!string.IsNullOrWhiteSpace(f.Category))query=query.Where(x=>x.Category==f.Category);
        if(!string.IsNullOrWhiteSpace(f.Search)){
            var term=f.Search.Trim();
            query=query.Where(x=>(x.Domain!=null && x.Domain.Contains(term)) || (x.Application!=null && x.Application.Contains(term))
                || x.SourceIp==term || x.DestinationIp==term || (x.ExactUrl!=null && x.ExactUrl.Contains(term)));
        }
        if(f.IncludeReverse!=true)query=query.Where(x=>x.Domain==null || (x.Domain!="localhost" && !x.Domain.EndsWith(".localhost") &&
            !x.Domain.EndsWith(".in-addr.arpa") && !x.Domain.EndsWith(".ip6.arpa")));
        var tz=TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
        if(f.From.HasValue){var from=TimeZoneInfo.ConvertTimeToUtc(f.From.Value.ToDateTime(TimeOnly.MinValue),tz);query=query.Where(x=>x.TimestampUtc>=from);}
        if(f.Through.HasValue && f.Through<DateOnly.MaxValue){var end=TimeZoneInfo.ConvertTimeToUtc(f.Through.Value.AddDays(1).ToDateTime(TimeOnly.MinValue),tz);query=query.Where(x=>x.TimestampUtc<end);}
        if(f.Before.HasValue && f.BeforeId.HasValue){
            var before=f.Before.Value.UtcDateTime;
            query=query.Where(x=>x.TimestampUtc<before || (x.TimestampUtc==before && x.Id<f.BeforeId));
        }
        return query.OrderByDescending(x=>x.TimestampUtc).ThenByDescending(x=>x.Id);
    }
}
