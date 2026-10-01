namespace Blocks.Genesis;

/// <summary>
/// The whole decision, in one atomic region. Check-then-increment as two commands is a race;
/// everything that decides an outcome happens inside this script, executed by Redis's single
/// thread — no locks, no compare-and-swap retry loop, no coordination between services.
/// </summary>
internal static class QuotaScript
{
    /// <summary>
    /// KEYS[1] counter hash · KEYS[2] idempotency key<br/>
    /// ARGV[1] amount · ARGV[2] idempotency ttl seconds · ARGV[3] counter ttl seconds<br/>
    /// returns { outcome, remaining, limit } where outcome is
    /// 0 allowed · 1 denied · 2 duplicate · 3 not loaded
    /// </summary>
    public const string ConsumeLua = @"
local seen = redis.call('SET', KEYS[2], '1', 'NX', 'EX', ARGV[2])
if not seen then
  return {2, -1, -1}
end

if redis.call('EXISTS', KEYS[1]) == 0 then
  redis.call('DEL', KEYS[2])
  return {3, -1, -1}
end

local limit     = tonumber(redis.call('HGET', KEYS[1], 'limit'))     or 0
local used      = tonumber(redis.call('HGET', KEYS[1], 'used'))      or 0
local purchased = tonumber(redis.call('HGET', KEYS[1], 'purchased')) or 0
local delta     = tonumber(ARGV[1])

-- Giving units back: deleting an agent is creating one with the sign flipped.
-- Never allowed below zero, and never refused.
if delta < 0 then
  local back = used + delta
  if back < 0 then back = 0 end
  redis.call('HSET', KEYS[1], 'used', back)
  redis.call('EXPIRE', KEYS[1], ARGV[3])
  if limit < 0 then return {0, -1, -1} end
  return {0, limit + purchased - back, limit}
end

-- limit -1 means uncapped: no ceiling to test.
if limit >= 0 and (used + delta) > (limit + purchased) then
  redis.call('DEL', KEYS[2])
  return {1, limit + purchased - used, limit}
end

local now = redis.call('HINCRBY', KEYS[1], 'used', delta)
redis.call('EXPIRE', KEYS[1], ARGV[3])
if limit < 0 then return {0, -1, -1} end
return {0, limit + purchased - now, limit}
";

    /// <summary>
    /// Seeds a counter only if it is absent, so concurrent seeders are harmless and no stampede
    /// lock is needed.<br/>
    /// KEYS[1] counter hash · ARGV[1] limit · ARGV[2] used · ARGV[3] purchased · ARGV[4] ttl seconds
    /// </summary>
    public const string SeedLua = @"
if redis.call('EXISTS', KEYS[1]) == 1 then
  return 0
end
redis.call('HSET', KEYS[1], 'limit', ARGV[1], 'used', ARGV[2], 'purchased', ARGV[3])
redis.call('EXPIRE', KEYS[1], ARGV[4])
return 1
";
}
