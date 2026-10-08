-- ASFALTO BetterTraffic client script, sent by the server when AiParams.BetterTraffic.ClientEffects is on.
-- * Big crashes: the player who hits a traffic car simulates it with CSP rigid body physics (real track collisions:
--   walls, ground, tumbling), sends it to the server ~15 times a second, and the server shows it to everybody.
-- * Parts (bumpers, mirrors, spoilers...) come off crashed traffic and bounce around, on every player's screen.
-- * Sparks, dust, wreck smoke and dirt; heavier contact when you hit traffic.
-- Problems are written to the CSP Lua debug log (prefix "BetterTraffic:").

local cfg = ac.configValues({
  AI_SLOTS = '', TRAFFIC_MASS = 3000, CONTACT_WEIGHT = 0.6, CLIENT_PHYSICS = 1, HEAVY_KPH = 50,
  PARTS = 1, PART_NAMES = 'bumper|mirror|spoiler', MAX_PARTS = 6,
})

local function log(message) ac.log('BetterTraffic: ' .. message) end
local function num(v, default) return tonumber(v) or default end

local trafficMass = num(cfg.TRAFFIC_MASS, 3000)
local contactWeight = num(cfg.CONTACT_WEIGHT, 0.6)
local heavyKph = num(cfg.HEAVY_KPH, 50)
local clientPhysics = num(cfg.CLIENT_PHYSICS, 1) ~= 0
local partsEnabled = num(cfg.PARTS, 1) ~= 0
local maxParts = num(cfg.MAX_PARTS, 6)

local isTraffic = {}
for id in string.gmatch(tostring(cfg.AI_SLOTS), '%d+') do isTraffic[tonumber(id)] = true end
local partFilters = {}
for rawName in string.gmatch(tostring(cfg.PART_NAMES), '[^,|]+') do
  local name = rawName:gsub('^%s+', ''):gsub('%s+$', '')
  if #name > 0 then
    local cap = name:sub(1, 1):upper() .. name:sub(2)
    -- CSP filters: '?' is the wildcard, ',' means or
    partFilters[#partFilters + 1] = '{ ?' .. name .. '?, ?' .. cap .. '?, ?' .. name:upper() .. '? }'
  end
end

local clock = 0

-- ── particles ──────────────────────────────────────────────────────────────────────────────────────────────────

local sparks = ac.Particles.Sparks({ color = rgbm(1, 0.62, 0.3, 2.5), life = 0.7, size = 0.12, directionSpread = 1.2, positionSpread = 0.5 })
local smoke = ac.Particles.Smoke({ color = rgbm(0.3, 0.3, 0.3, 0.5), colorConsistency = 0.4, thickness = 0.8, life = 7, size = 0.6,
  spreadK = 0.8, growK = 1.4, targetYVelocity = 0.8 })
local dust = ac.Particles.Smoke({ color = rgbm(0.55, 0.5, 0.42, 0.4), colorConsistency = 0.6, thickness = 0.6, life = 3, size = 0.9,
  spreadK = 1.5, growK = 1.6, targetYVelocity = 0.1 })

local glass = ac.Particles.Sparks({ color = rgbm(0.75, 0.85, 1, 1.2), life = 1.4, size = 0.05, directionSpread = 2, positionSpread = 0.8 })

local function burst(position, impactKph, withDust)
  sparks:emit(position + vec3(0, 0.4, 0), vec3(0, 2.5, 0), math.min(80, 10 + impactKph * 0.6))
  if impactKph > 45 then glass:emit(position + vec3(0, 0.9, 0), vec3(0, 3, 0), math.min(60, impactKph * 0.4)) end
  if withDust then dust:emit(position + vec3(0, 0.3, 0), vec3(0, 0.5, 0), math.min(16, 2 + impactKph / 8)) end
end

-- ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────────

local function cross(a, b) return vec3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x) end

-- AC rotation (heading, pitch, roll) from direction vectors, the same way AssettoServer writes traffic rotations.
-- The roll direction isn't documented: rollSign starts at 1 and is corrected by comparing what we send with what we
-- get back from the server (see checkRollSign).
local rollSign = 1
local function rotationFrom(look, up)
  local heading = math.atan2(look.z, look.x) - math.pi / 2
  local pitch = math.asin(math.max(-1, math.min(1, look.y)))
  local right0 = cross(look, vec3(0, 1, 0))
  local roll = 0
  if right0:length() > 1e-3 then
    right0:normalize()
    local up0 = cross(right0, look)
    roll = math.atan2(up:dot(right0), up:dot(up0))
  end
  return heading, pitch, roll
end

local function nearestTraffic(position, maxDistance)
  local best, bestDistance = nil, maxDistance
  for i = 1, sim.carsCount - 1 do
    if isTraffic[i] then
      local car = ac.getCar(i)
      if car then
        local d = car.position:distance(position)
        if d < bestDistance then best, bestDistance = car, d end
      end
    end
  end
  return best
end

-- ── damage look ────────────────────────────────────────────────────────────────────────────────────────────────

-- car index -> { level 0..1, position, smoke, expires, applied, smokeAcc, parts = { ... } }
local damaged = {}

local function setDamage(index, level)
  local hit = level * 120 -- km/h of damage per side, like AC's own damage model
  pcall(physics.setCarBodyDamage, index, vec4(hit, hit * 0.6, hit * 0.8, hit * 0.4))
  pcall(ac.setBodyDirt, index, math.min(1, level * 1.2))
end

-- ── loose parts ────────────────────────────────────────────────────────────────────────────────────────────────
-- Parts are moved by this script (no CSP physics needed): thrown off with the impact, tumbling, bouncing and
-- sliding on the road until they stop. Named parts (PART_NAMES) first, otherwise any mid-sized piece of the model.

local dynamicRoot = nil
local activeParts = 0
local MAX_ACTIVE_PARTS = 30
local GRAVITY = 9.81

local function rotate(v, axis, angle)
  -- Rodrigues: v rotated around a unit axis
  local c, s = math.cos(angle), math.sin(angle)
  return v * c + cross(axis, v) * s + axis * (axis:dot(v) * (1 - c))
end

local function partCandidates(root)
  local named, sized = {}, {}
  for _, filter in ipairs(partFilters) do
    local ok, meshes = pcall(root.findMeshes, root, filter)
    if ok and meshes then
      for i = 1, meshes:size() do named[#named + 1] = meshes:at(i) end
    end
  end
  if #named > 0 then return named end
  -- no names matched (traffic models often have generic mesh names): any mid-sized piece of the visible model
  local ok, meshes = pcall(root.findMeshes, root, '{ lod:A }')
  if not ok or not meshes or meshes:size() == 0 then ok, meshes = pcall(root.findMeshes, root, '?') end
  if ok and meshes then
    for i = 1, meshes:size() do
      local mesh = meshes:at(i)
      local okSphere, _, radius = pcall(mesh.boundingSphere, mesh)
      if okSphere and radius and radius > 0.12 and radius < 0.9 then sized[#sized + 1] = mesh end
    end
  end
  return sized
end

local function detachParts(index, car, impactKph, impulseDir)
  if not partsEnabled then return {} end
  local count = math.min(maxParts, 1 + math.floor(impactKph / 30), MAX_ACTIVE_PARTS - activeParts)
  if count <= 0 then return {} end
  if not dynamicRoot then dynamicRoot = ac.findNodes('dynamicRoot:yes') end
  local root = ac.findNodes('carRoot:' .. index)
  if not root or root:empty() then return {} end

  local candidates = partCandidates(root)
  local parts = {}
  while #parts < count and #candidates > 0 do
    local mesh = table.remove(candidates, math.random(#candidates))
    local ok, err = pcall(function()
      local parent = mesh:getParent()
      local savedLocal = mesh:getTransformationRaw():clone()
      local world = mesh:getWorldTransformationRaw():clone()
      local _, radius = mesh:boundingSphere()
      radius = math.max(0.1, math.min(0.9, radius or 0.3))
      -- lighter parts fly further
      local throw = (impactKph / 3.6) * (0.1 + 0.15 * math.random()) / (0.6 + radius)
      local velocity = car.velocity * 0.7 + impulseDir * throw
        + vec3(math.random() - 0.5, 0.5 + math.random() * 0.6, math.random() - 0.5) * (1.5 + impactKph / 30)
      local axis = vec3(math.random() - 0.5, math.random() - 0.5, math.random() - 0.5)
      if axis:length() < 0.01 then axis = vec3(1, 0, 0) end
      mesh:setParent(dynamicRoot)
      mesh:getTransformationRaw():set(world)
      parts[#parts + 1] = {
        mesh = mesh, parent = parent, savedLocal = savedLocal,
        position = world.position:clone(), look = world.look:clone(), up = world.up:clone(),
        velocity = velocity, axis = axis:normalize(), spin = (4 + math.random() * 10) / (0.5 + radius),
        ground = car.position.y + 0.02, radius = radius, moving = true, until_ = clock + 25,
      }
      activeParts = activeParts + 1
    end)
    if not ok then log('part could not come off: ' .. tostring(err)) end
  end
  if #parts == 0 then log('no loose parts found on car ' .. index) end
  return parts
end

local function stopPart(part)
  if part.moving then
    part.moving = false
    activeParts = activeParts - 1
  end
end

local function restoreParts(d)
  for _, part in ipairs(d.parts or {}) do
    stopPart(part)
    pcall(function()
      part.mesh:setParent(part.parent)
      part.mesh:getTransformationRaw():set(part.savedLocal)
    end)
  end
  d.parts = {}
end

local function updateParts(d, dt)
  for _, part in ipairs(d.parts or {}) do
    if part.moving then
      local v = part.velocity
      v.y = v.y - GRAVITY * dt
      local drag = math.max(0, 1 - 0.4 * dt) -- tumbling parts are draggy
      v.x, v.z = v.x * drag, v.z * drag
      part.position = part.position + v * dt
      local onGround = part.position.y <= part.ground + part.radius * 0.3
      if onGround then
        part.position.y = part.ground + part.radius * 0.3
        if v.y < -1.5 then
          -- bounce, with a few sparks for the hard ones
          v.y = -v.y * 0.35
          v.x, v.z = v.x * 0.6, v.z * 0.6
          if math.abs(v.y) > 2 then sparks:emit(part.position, vec3(0, 1, 0), 4) end
        else
          v.y = 0
        end
        -- scraping along the road
        local horizontal = vec3(v.x, 0, v.z)
        local speed = horizontal:length()
        local slowed = math.max(0, speed - 14 * dt)
        if speed > 0 then v.x, v.z = v.x * slowed / speed, v.z * slowed / speed end
        part.spin = part.spin * math.max(0, 1 - 3 * dt)
      end
      local angle = part.spin * dt
      part.look = rotate(part.look, part.axis, angle)
      part.up = rotate(part.up, part.axis, angle)
      pcall(function() part.mesh:getTransformationRaw():set(mat4x4.look(part.position, part.look, part.up)) end)
      if (onGround and v:length() < 0.2 and part.spin < 0.3) or clock > part.until_ then stopPart(part) end
    end
  end
end

-- ── crash physics (only on the hitting player's game) ──────────────────────────────────────────────────────────

local sendPhysics = ac.OnlineEvent({
  ac.StructItem.key('ASFALTO_BetterTrafficPhysics'),
  carIndex = ac.StructItem.uint8(),
  position = ac.StructItem.vec3(),
  rotation = ac.StructItem.vec3(),
  velocity = ac.StructItem.vec3(),
  impactKph = ac.StructItem.float(),
  resting = ac.StructItem.uint8(),
}, function() end)

local sims = {} -- car index -> { body, started, lastSend, still, impactKph, sentRolls = {} }

local function sendSim(index, s, resting)
  local t = s.body:getTransformation()
  local h, p, r = rotationFrom(t.look, t.up)
  local v = resting and vec3() or s.body:getVelocity()
  sendPhysics({
    carIndex = index, position = t.position, rotation = vec3(h, p, r * rollSign), velocity = v,
    impactKph = s.firstSent and 0 or s.impactKph, resting = resting and 1 or 0,
  }, false, 255)
  s.firstSent = true
  s.sentRolls[#s.sentRolls + 1] = { time = clock, roll = r }
  if #s.sentRolls > 30 then table.remove(s.sentRolls, 1) end
end

local function endSim(index, s)
  pcall(sendSim, index, s, true)
  pcall(s.body.dispose, s.body)
  pcall(physics.disableCarCollisions, index, false, true)
  sims[index] = nil
end

local function startSim(index, car, normal, closing, impactKph, contact)
  if sims[index] then return end
  local ok, err = pcall(function()
    local size = car.aabbSize and car.aabbSize:length() > 1 and car.aabbSize or vec3(1.8, 1.4, 4.5)
    local center = car.aabbSize and car.aabbSize:length() > 1 and car.aabbCenter or vec3(0, 0.7, 0)
    local body = physics.RigidBody({ physics.Collider.Box(size, center) }, trafficMass)
    body:setTransformation(mat4x4.look(car.position, car.look, car.up), false)
    -- momentum from our car (lighter than traffic) and spin from an off-centre hit
    local me = ac.getCar(0)
    local myMass = me.mass > 0 and me.mass or 1400
    local dv = normal * (closing * myMass / (myMass + trafficMass) * 1.3)
    body:setVelocity(car.velocity + dv)
    local r = contact - car.position
    body:setAngularVelocity(cross(r, dv) * 0.5)
    -- the traffic car's own collider would fight the body: the body takes over collisions
    pcall(physics.disableCarCollisions, index, true, true)
    sims[index] = { body = body, started = clock, lastSend = -1, still = 0, impactKph = impactKph, sentRolls = {} }
  end)
  if not ok then
    log('crash physics not available, the server simulates the crash instead (' .. tostring(err) .. ')')
    clientPhysics = false
  end
end

-- What the server shows (rendered from the rotation we sent) must roll the same way as our body. If it consistently
-- rolls the other way, the roll convention is the other way round: flip it.
local rollVotes = 0
local function checkRollSign(index, s)
  local car = ac.getCar(index)
  if not car then return end
  local _, _, shownRoll = rotationFrom(car.look, car.up)
  local sent = s.sentRolls[math.max(1, #s.sentRolls - 3)] -- what we sent ~200 ms ago
  if not sent or math.abs(sent.roll) < 0.5 or math.abs(shownRoll) < 0.5 then return end
  if (shownRoll > 0) ~= (sent.roll > 0) then rollVotes = rollVotes + 1 else rollVotes = math.max(0, rollVotes - 1) end
  if rollVotes >= 8 then
    rollSign = -rollSign
    rollVotes = 0
    log('roll direction corrected to ' .. rollSign)
  end
end

local function updateSims(dt)
  for index, s in pairs(sims) do
    local ok = pcall(function()
      local speed = s.body:getVelocity():length()
      local spin = s.body:getAngularVelocity():length()
      s.still = (speed < 0.5 and spin < 0.3) and s.still + dt or 0
      if s.still > 1.5 or clock - s.started > 15 then
        endSim(index, s)
        return
      end
      if clock - s.lastSend > 1 / 15 then
        s.lastSend = clock
        sendSim(index, s, false)
        checkRollSign(index, s)
      end
    end)
    if not ok and sims[index] then endSim(index, s) end
  end
end

-- ── server messages ────────────────────────────────────────────────────────────────────────────────────────────

local PHASE_HIT, PHASE_SETTLED, PHASE_CLEARED = 0, 1, 2
local MODE_PULLOVER = 1

ac.OnlineEvent({
  ac.StructItem.key('ASFALTO_BetterTrafficCrash'),
  carIndex = ac.StructItem.uint8(),
  phase = ac.StructItem.uint8(),
  mode = ac.StructItem.uint8(),
  position = ac.StructItem.vec3(),
  impactKph = ac.StructItem.float(),
  simulatedBy = ac.StructItem.uint8(),
}, function(sender, data)
  if sender ~= nil then return end -- only the server sends these
  local index = data.carIndex
  if data.phase == PHASE_CLEARED then
    local d = damaged[index]
    if d then
      if d.applied then setDamage(index, 0) end
      restoreParts(d)
    end
    damaged[index] = nil
    if sims[index] then endSim(index, sims[index]) end
    return
  end

  local minor = data.mode == MODE_PULLOVER
  local d = damaged[index] or { level = 0, applied = false, parts = {} }
  d.level = math.max(d.level, math.min(1, data.impactKph / 120))
  d.position = data.position
  d.smoke = not minor and data.impactKph > 60
  d.expires = clock + (minor and 60 or 240)
  damaged[index] = d

  if data.phase == PHASE_HIT then
    burst(data.position, data.impactKph, not minor)
    -- parts: on everyone's screen, unless we already knocked them off ourselves
    local car = ac.getCar(index)
    if car and not minor and #d.parts == 0 and data.impactKph >= heavyKph then
      local away = car.velocity:length() > 1 and car.velocity:clone():normalize() or car.look
      for _, part in ipairs(detachParts(index, car, data.impactKph, away)) do d.parts[#d.parts + 1] = part end
    end
  end
end)

-- ── our own hits ───────────────────────────────────────────────────────────────────────────────────────────────

local lastHit = -10
ac.onCarCollision(0, function()
  local me = ac.getCar(0)
  if not me or clock - lastHit < 0.3 then return end
  local car = nearestTraffic(me.position, 7)
  if not car then return end

  local normal = car.position - me.position
  normal.y = 0
  if normal:length() < 0.01 then return end
  normal:normalize()
  local closing = (me.velocity - car.velocity):dot(normal)
  if closing < 1 then return end
  lastHit = clock
  local impactKph = closing * 3.6
  local contact = (me.position + car.position) / 2
  burst(contact, impactKph, closing > 8)

  if impactKph >= heavyKph and clientPhysics and not sims[car.index] then
    startSim(car.index, car, normal, closing, impactKph, contact)
    local d = damaged[car.index] or { level = 0, applied = false, parts = {} }
    d.level = math.max(d.level, math.min(1, impactKph / 120))
    d.position = car.position
    d.smoke = impactKph > 60
    d.expires = clock + 240
    damaged[car.index] = d
    if #d.parts == 0 then
      for _, part in ipairs(detachParts(car.index, car, impactKph, normal)) do d.parts[#d.parts + 1] = part end
    end
  elseif contactWeight > 0 then
    -- smaller hits: traffic feels heavy, an extra push on our car as if it weighed TRAFFIC_MASS
    local myMass = me.mass > 0 and me.mass or 1400
    local extra = (trafficMass / (myMass + trafficMass) - 0.5) * 1.3 * closing * contactWeight
    if extra > 0 then pcall(physics.setCarVelocity, 0, me.velocity - normal * extra) end
  end
end)

-- ── every frame ────────────────────────────────────────────────────────────────────────────────────────────────

function script.update(dt)
  clock = clock + dt
  updateSims(dt)
  for index, d in pairs(damaged) do
    local car = ac.getCar(index)
    -- with traffic overbooking a car slot can show another traffic car for you: only damage it near the crash
    local near = car and d.position and (car.position:distance(d.position) < 40 or sims[index] ~= nil)
    if clock > d.expires or not car then
      if d.applied then setDamage(index, 0) end
      restoreParts(d)
      damaged[index] = nil
    else
      if near then d.position = car.position end -- follow the wreck while it moves
      if near and not d.applied then
        setDamage(index, d.level)
        d.applied = true
      elseif not near and d.applied then
        setDamage(index, 0)
        d.applied = false
      end
      updateParts(d, dt)
      if near and d.smoke then
        -- smoke from the engine bay, a few puffs per second, emitted as whole particles
        d.smokeAcc = (d.smokeAcc or 0) + dt * 5 * d.level
        if d.smokeAcc >= 1 then
          local puffs = math.floor(d.smokeAcc)
          d.smokeAcc = d.smokeAcc - puffs
          smoke:emit(car.position + car.look * 1.4 + vec3(0, 0.9, 0), vec3(0, 0.7, 0), puffs)
        end
      end
    end
  end
end

log('loaded (client physics ' .. tostring(clientPhysics) .. ', parts ' .. tostring(partsEnabled) .. ', ' .. #partFilters .. ' part names)')
