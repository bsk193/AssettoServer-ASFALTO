-- ASFALTO BetterTraffic client effects, sent by the server when AiParams.BetterTraffic.ClientEffects is on.
-- * sparks when you hit traffic, and sparks + smoke for every traffic crash anyone has (the server tells everyone)
-- * crashed traffic looks damaged and dirty, wrecks keep smoking until the server clears them
-- * traffic feels heavy: an extra push on your own car when you hit it (TrafficMassKg, ContactWeightFactor)

local cfg = ac.configValues({ AI_SLOTS = '', TRAFFIC_MASS = 3000, CONTACT_WEIGHT = 0.6 })

local isTraffic = {}
for id in string.gmatch(tostring(cfg.AI_SLOTS), '%d+') do isTraffic[tonumber(id)] = true end

local sparks = ac.Particles.Sparks({ color = rgbm(1, 0.62, 0.3, 2.5), life = 0.7, size = 0.12, directionSpread = 1.2, positionSpread = 0.5 })
local smoke = ac.Particles.Smoke({ color = rgbm(0.32, 0.32, 0.32, 0.45), colorConsistency = 0.4, thickness = 0.7, life = 6, size = 0.5,
  spreadK = 0.8, growK = 1.3, targetYVelocity = 0.7 })
local dust = ac.Particles.Smoke({ color = rgbm(0.55, 0.5, 0.42, 0.35), colorConsistency = 0.6, thickness = 0.5, life = 3, size = 0.8,
  spreadK = 1.5, growK = 1.6, targetYVelocity = 0.1 })

local PHASE_HIT, PHASE_SETTLED, PHASE_CLEARED = 0, 1, 2
local MODE_PULLOVER = 1

-- car index -> { level 0..1, position, smoke (bool), expires (s), applied (bool) }
local damaged = {}
local clock = 0

local function burst(position, impactKph, withSmoke)
  local amount = math.min(60, 8 + impactKph * 0.5)
  sparks:emit(position + vec3(0, 0.4, 0), vec3(0, 2.5, 0), amount)
  if withSmoke then dust:emit(position + vec3(0, 0.3, 0), vec3(0, 0.5, 0), math.min(12, impactKph / 10)) end
end

local function setDamage(index, level)
  local hit = level * 120 -- km/h of damage per side, like AC's own damage model
  pcall(physics.setCarBodyDamage, index, vec4(hit, hit * 0.6, hit * 0.8, hit * 0.4))
  pcall(ac.setBodyDirt, index, math.min(1, level * 1.2))
end

ac.OnlineEvent({
  ac.StructItem.key('ASFALTO_BetterTrafficCrash'),
  phase = ac.StructItem.uint8(),
  mode = ac.StructItem.uint8(),
  position = ac.StructItem.vec3(),
  impactKph = ac.StructItem.float(),
}, function(sender, data)
  if not sender then return end
  local index = sender.index
  if data.phase == PHASE_CLEARED then
    local d = damaged[index]
    if d and d.applied then setDamage(index, 0) end
    damaged[index] = nil
    return
  end

  local minor = data.mode == MODE_PULLOVER
  local level = math.min(1, data.impactKph / 120)
  if data.phase == PHASE_HIT then
    burst(data.position, data.impactKph, not minor)
  end
  local d = damaged[index] or { level = 0, applied = false }
  d.level = math.max(d.level, level)
  d.position = data.position
  d.smoke = not minor and data.impactKph > 60
  d.expires = clock + (minor and 60 or 180)
  damaged[index] = d
end)

-- heavy traffic: hitting a traffic car pushes your car back as if the traffic car weighed TRAFFIC_MASS
local lastPush = -10
ac.onCarCollision(0, function()
  local me = ac.getCar(0)
  if not me or clock - lastPush < 0.3 then return end
  local best, bestDistance = nil, 7
  for i = 1, sim.carsCount - 1 do
    local car = ac.getCar(i)
    if car and isTraffic[i] then
      local distance = car.position:distance(me.position)
      if distance < bestDistance then best, bestDistance = car, distance end
    end
  end
  if not best then return end

  local normal = (me.position - best.position)
  normal.y = 0
  if normal:length() < 0.01 then return end
  normal:normalize()
  local closing = -(me.velocity - best.velocity):dot(normal)
  if closing < 1 then return end
  lastPush = clock

  burst((me.position + best.position) / 2, closing * 3.6, closing > 8)

  local weight = tonumber(cfg.CONTACT_WEIGHT) or 0
  if weight > 0 then
    local myMass = me.mass > 0 and me.mass or 1400
    local trafficMass = tonumber(cfg.TRAFFIC_MASS) or 3000
    -- share of the closing speed the traffic car's extra weight adds, on top of AC's own (equal-ish mass) contact
    local extra = (trafficMass / (myMass + trafficMass) - 0.5) * 1.3 * closing * weight
    if extra > 0 then pcall(physics.setCarVelocity, 0, me.velocity + normal * extra) end
  end
end)

function script.update(dt)
  clock = clock + dt
  for index, d in pairs(damaged) do
    local car = ac.getCar(index)
    -- with traffic overbooking a car slot can show another traffic car for you: only damage it near the crash
    local near = car and d.position and car.position:distance(d.position) < 40
    if clock > d.expires or not car then
      if d.applied then setDamage(index, 0) end
      damaged[index] = nil
    else
      if near and not d.applied then
        setDamage(index, d.level)
        d.applied = true
      elseif not near and d.applied then
        setDamage(index, 0)
        d.applied = false
      end
      if near and d.smoke and car.speedKmh < 5 then
        -- a few puffs per second, emitted as whole particles
        d.smokeAcc = (d.smokeAcc or 0) + dt * 6 * d.level
        if d.smokeAcc >= 1 then
          local puffs = math.floor(d.smokeAcc)
          d.smokeAcc = d.smokeAcc - puffs
          smoke:emit(car.position + vec3(0, 0.9, 0), vec3(0, 0.6, 0), puffs)
        end
      end
    end
  end
end
