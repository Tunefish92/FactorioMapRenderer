-- Factorio Map Renderer export.
--
-- The renderer adds this mod to a copy of the mod list and runs the save for one benchmark tick. A mod that
-- is new to a save gets on_init, which runs with the whole map loaded: everything the map shows is written to
-- script-output/map-export as plain text, one file per surface, and the game ends. The save is never written.
--
-- Lines of a surface file (fields separated by tabs):
--   S  index  json            the surface: name, planet, platform
--   T  code  name  r  g  b    a tile prototype, with its map colour
--   P  code  name  json       an entity prototype: type, map colours, size
--   C  cx  cy  tiles          a charted chunk: 32 x 32 tile codes row by row, runs written as code*count
--   E  proto  force  x1  y1  x2  y2  direction  x  y  [json]
--                             an entity with its bounding box; json holds the details shown on a click
--   R  proto  x  y  amount    a resource tile
--   N  proto  x  y            a tree, rock or other neutral obstacle
--   W  x1  y1  x2  y2         a copper wire between two poles
--   G  json                   a map tag
--   X  name  count            entities counted but not written (biters, items on the ground, corpses)

local write_file = (helpers and helpers.write_file) or game.write_file
local to_json = (helpers and helpers.table_to_json) or game.table_to_json
local entity_prototypes = (prototypes and prototypes.entity) or game.entity_prototypes

local OUT = "map-export/"
local FLUSH_LINES = 4000

-- Too many, too short-lived or not on the map: only counted.
local COUNT_ONLY = {
  ["unit"] = true, ["item-entity"] = true, ["corpse"] = true, ["character-corpse"] = false, ["rail-remnants"] = true,
  ["entity-ghost"] = true, ["tile-ghost"] = true, ["item-request-proxy"] = true, ["deconstructible-tile-proxy"] = true,
  ["projectile"] = true, ["fire"] = true, ["stream"] = true, ["sticker"] = true, ["smoke-with-trigger"] = true,
  ["explosion"] = true, ["highlight-box"] = true, ["speech-bubble"] = true, ["beam"] = true, ["artillery-flare"] = true,
  ["artillery-projectile"] = true, ["particle-source"] = true, ["combat-robot"] = true, ["construction-robot"] = true,
  ["logistic-robot"] = true, ["capture-robot"] = true, ["segment"] = true, ["spider-leg"] = true,
  ["asteroid"] = true, ["temporary-container"] = true,
}
local NATURE = { ["tree"] = true, ["simple-entity"] = true, ["fish"] = true, ["cliff"] = true, ["plant"] = true }

-- Containers whose contents are shown on a click: inventory index by type.
local CONTENTS = {
  ["container"] = "chest", ["logistic-container"] = "chest", ["infinity-container"] = "chest", ["linked-container"] = "chest",
  ["cargo-wagon"] = "cargo_wagon", ["space-platform-hub"] = "hub_main", ["cargo-landing-pad"] = "cargo_landing_pad_main",
  ["rocket-silo"] = "rocket_silo_rocket", ["car"] = "car_trunk", ["spider-vehicle"] = "spider_trunk",
}
local DETAILED = {
  ["assembling-machine"] = true, ["furnace"] = true, ["mining-drill"] = true, ["lab"] = true, ["train-stop"] = true,
  ["rocket-silo"] = true, ["roboport"] = true, ["radar"] = true, ["boiler"] = true, ["generator"] = true,
  ["reactor"] = true, ["storage-tank"] = true, ["beacon"] = true, ["locomotive"] = true, ["fluid-wagon"] = true,
  ["artillery-turret"] = true, ["ammo-turret"] = true, ["electric-turret"] = true, ["fluid-turret"] = true,
  ["unit-spawner"] = true, ["turret"] = true, ["accumulator"] = true, ["solar-panel"] = true, ["pump"] = true,
  ["offshore-pump"] = true, ["agricultural-tower"] = true, ["fusion-reactor"] = true, ["fusion-generator"] = true,
  ["asteroid-collector"] = true, ["thruster"] = true, ["lightning-attractor"] = true, ["heating-tower"] = true,
  ["electric-energy-interface"] = true, ["burner-generator"] = true, ["character"] = true,
  -- only which end they are, for drawing them
  ["underground-belt"] = true, ["loader"] = true, ["loader-1x1"] = true,
  -- where they pick up and their filters, for drawing them and for the alt mode
  ["inserter"] = true, ["splitter"] = true, ["lane-splitter"] = true,
}
for type in pairs(CONTENTS) do DETAILED[type] = true end

local status_names = {}
if defines.entity_status then
  for name, value in pairs(defines.entity_status) do status_names[value] = name end
end

local function color_of(c)
  if not c then return nil end
  local r, g, b = c.r or c[1] or 0, c.g or c[2] or 0, c.b or c[3] or 0
  -- colours may be given as 0..1 or 0..255
  if r <= 1 and g <= 1 and b <= 1 then r, g, b = r * 255, g * 255, b * 255 end
  return { math.floor(r + 0.5), math.floor(g + 0.5), math.floor(b + 0.5) }
end

local function round(v) return math.floor(v * 100 + 0.5) / 100 end

local Writer = {}
Writer.__index = Writer
function Writer.new(file)
  write_file(OUT .. file, "", false)
  return setmetatable({ file = OUT .. file, lines = {}, n = 0 }, Writer)
end
function Writer:line(s)
  local n = self.n + 1
  self.lines[n] = s
  self.n = n
  if n >= FLUSH_LINES then self:flush() end
end
function Writer:flush()
  if self.n == 0 then return end
  write_file(self.file, table.concat(self.lines, "\n", 1, self.n) .. "\n", true)
  self.lines = {}
  self.n = 0
end

local function contents_of(inventory)
  if not inventory then return nil end
  local list = {}
  local ok, contents = pcall(inventory.get_contents)
  if not ok or not contents then return nil end
  for key, value in pairs(contents) do
    if type(value) == "table" then
      -- 2.0: { name, count, quality }
      list[#list + 1] = { value.name, value.count, value.quality ~= "normal" and value.quality or nil }
    else
      list[#list + 1] = { key, value }
    end
  end
  if #list == 0 then return nil end
  table.sort(list, function(a, b) return a[2] > b[2] end)
  return list
end

local function details_of(entity, type)
  local d = {}
  local ok, value
  if entity.unit_number then d.id = entity.unit_number end
  ok, value = pcall(function() return entity.status end)
  if ok and value then d.status = status_names[value] end
  ok, value = pcall(function() return entity.quality and entity.quality.name end)
  if ok and value and value ~= "normal" then d.quality = value end
  if type == "assembling-machine" or type == "furnace" or type == "rocket-silo" then
    ok, value = pcall(entity.get_recipe)
    if ok and value then d.recipe = value.name end
    if type == "furnace" and not d.recipe then
      ok, value = pcall(function() return entity.previous_recipe end)
      if ok and value then d.recipe = value.name or (value[1] and value[1].name) end
    end
  end
  if type == "mining-drill" then
    ok, value = pcall(function() return entity.mining_target end)
    if ok and value then d.mining = value.name end
  end
  ok, value = pcall(function() return entity.backer_name end)
  if ok and value and value ~= "" then d.name = value end
  if type == "train-stop" then
    ok, value = pcall(function() return entity.trains_limit end)
    if ok and value and value < 4294967295 then d.limit = value end
  end
  if type == "locomotive" or type == "cargo-wagon" or type == "fluid-wagon" then
    ok, value = pcall(function() return entity.train and entity.train.id end)
    if ok and value then d.train = value end
  end
  if type == "storage-tank" or type == "fluid-wagon" or type == "boiler" or type == "reactor" then
    ok, value = pcall(entity.get_fluid_contents)
    if ok and value then
      local fluids = {}
      for name, amount in pairs(value) do fluids[#fluids + 1] = { name, math.floor(amount) } end
      if #fluids > 0 then d.fluids = fluids end
    end
  end
  local inventory = CONTENTS[type]
  if inventory and defines.inventory[inventory] then
    ok, value = pcall(entity.get_inventory, defines.inventory[inventory])
    if ok then d.items = contents_of(value) end
  end
  if type == "underground-belt" then
    ok, value = pcall(function() return entity.belt_to_ground_type end)
    if ok then d.io = value end
  elseif type == "loader" or type == "loader-1x1" then
    ok, value = pcall(function() return entity.loader_type end)
    if ok then d.io = value end
  end
  -- modules: shown in the alt mode, as the game does
  ok, value = pcall(function() return entity.get_module_inventory() end)
  if ok and value then d.modules = contents_of(value) end
  if type == "inserter" then
    ok, value = pcall(function() return entity.pickup_position end)
    if ok and value then d.pickup = { round(value.x - entity.position.x), round(value.y - entity.position.y) } end
  end
  if type == "inserter" or type == "loader" or type == "loader-1x1" then
    ok, value = pcall(function()
      local filters = {}
      for i = 1, entity.filter_slot_count do
        local f = entity.get_filter(i)
        if f then filters[#filters + 1] = f.name or f end
      end
      return filters
    end)
    if ok and value and #value > 0 then d.filters = value end
  elseif type == "splitter" or type == "lane-splitter" then
    ok, value = pcall(function() local f = entity.splitter_filter; return f and (f.name or f) end)
    if ok and value then d.filters = { value } end
  end
  if type == "character" then
    ok, value = pcall(function() return entity.player and entity.player.name end)
    if ok and value then d.player = value end
  end
  if type == "unit-spawner" or type == "turret" then
    ok, value = pcall(function() return entity.force.name end)
    if ok then d.force = value end
  end
  return d
end

local function export_surface(surface, force, file_index)
  local w = Writer.new("surface-" .. file_index .. ".txt")
  local meta = { name = surface.name, index = surface.index }
  pcall(function() if surface.planet then meta.planet = surface.planet.name end end)
  pcall(function()
    local platform = surface.platform
    if platform then
      meta.platform = platform.name
      meta.location = platform.space_location and platform.space_location.name
      meta.state = platform.state
      -- on its way: the route and how far along it (0 to 1)
      if platform.space_connection then
        meta.connection = platform.space_connection.name
        meta.distance = platform.distance
      end
    end
  end)
  pcall(function() meta.seed = surface.map_gen_settings.seed end)
  pcall(function() meta.daytime = surface.daytime end)
  w:line("S\t" .. surface.index .. "\t" .. to_json(meta))

  local tile_codes, tile_count = {}, 0
  local proto_codes, proto_count = {}, 0
  local counted = {}
  local get_tile = surface.get_tile

  local function tile_code(name)
    local code = tile_codes[name]
    if code then return code end
    code = tile_count
    tile_count = tile_count + 1
    tile_codes[name] = code
    local c = { 0, 0, 0 }
    pcall(function() c = color_of(prototypes.tile[name].map_color) or c end)
    w:line("T\t" .. code .. "\t" .. name .. "\t" .. c[1] .. "\t" .. c[2] .. "\t" .. c[3])
    return code
  end

  local function proto_code(entity)
    local name = entity.name
    local code = proto_codes[name]
    if code then return code end
    code = proto_count
    proto_count = proto_count + 1
    proto_codes[name] = code
    local p = entity_prototypes[name]
    local info = { type = entity.type }
    if p then
      pcall(function() info.map = color_of(p.map_color) end)
      pcall(function() info.friendly = color_of(p.friendly_map_color) end)
      pcall(function() info.enemy = color_of(p.enemy_map_color) end)
      pcall(function() info.w, info.h = p.tile_width, p.tile_height end)
    end
    w:line("P\t" .. code .. "\t" .. name .. "\t" .. to_json(info))
    return code
  end

  local platform = surface.platform ~= nil
  -- Platforms are never charted, and neither is a new map nobody has joined yet (a server's): of these, what is
  -- generated, so that its ground shows rather than nothing.
  local whole = platform
  if not whole then
    whole = true
    for chunk in surface.get_chunks() do
      if force.is_chunk_charted(surface, { chunk.x, chunk.y }) then whole = false; break end
    end
  end
  local chunks = 0
  for chunk in surface.get_chunks() do
    local cx, cy = chunk.x, chunk.y
    local charted = force.is_chunk_charted(surface, { cx, cy })
    if not charted and whole then charted = surface.is_chunk_generated({ cx, cy }) end
    if charted then
      chunks = chunks + 1
      local x0, y0 = cx * 32, cy * 32
      -- tiles, run-length coded
      local runs, n = {}, 0
      local last, run = nil, 0
      for y = y0, y0 + 31 do
        for x = x0, x0 + 31 do
          local code = tile_code(get_tile(x, y).name)
          if code == last then
            run = run + 1
          else
            if last then n = n + 1; runs[n] = run > 1 and (last .. "*" .. run) or tostring(last) end
            last, run = code, 1
          end
        end
      end
      n = n + 1; runs[n] = run > 1 and (last .. "*" .. run) or tostring(last)
      w:line("C\t" .. cx .. "\t" .. cy .. "\t" .. table.concat(runs, ",", 1, n))

      -- entities whose position lies in this chunk (find_entities also returns the ones that only reach into it)
      for _, e in pairs(surface.find_entities_filtered({ area = { { x0, y0 }, { x0 + 32, y0 + 32 } } })) do
        local p = e.position
        local px, py = p.x, p.y
        if px >= x0 and px < x0 + 32 and py >= y0 and py < y0 + 32 then
          local type = e.type
          if COUNT_ONLY[type] then
            counted[e.name] = (counted[e.name] or 0) + 1
          elseif type == "resource" then
            w:line("R\t" .. proto_code(e) .. "\t" .. round(px) .. "\t" .. round(py) .. "\t" .. e.amount)
          elseif NATURE[type] then
            local line = "N\t" .. proto_code(e) .. "\t" .. round(px) .. "\t" .. round(py)
            -- a cliff's picture depends on which way it runs
            if type == "cliff" then line = line .. "\t" .. e.cliff_orientation end
            w:line(line)
          else
            local box = e.bounding_box
            local lt, rb = box.left_top, box.right_bottom
            local line = "E\t" .. proto_code(e) .. "\t" .. e.force.index .. "\t" .. round(lt.x) .. "\t" .. round(lt.y) .. "\t"
              .. round(rb.x) .. "\t" .. round(rb.y) .. "\t" .. (e.direction or 0) .. "\t" .. round(px) .. "\t" .. round(py)
            if DETAILED[type] then line = line .. "\t" .. to_json(details_of(e, type)) end
            w:line(line)
            if type == "electric-pole" then
              pcall(function()
                local connector = e.get_wire_connector(defines.wire_connector_id.pole_copper, false)
                if not connector then return end
                for _, connection in pairs(connector.real_connections) do
                  local other = connection.target.owner
                  -- each wire once: from the pole with the smaller number
                  if other.type ~= "electric-pole" or (other.unit_number or 0) > (e.unit_number or 0) then
                    local q = other.position
                    w:line("W\t" .. round(px) .. "\t" .. round(py) .. "\t" .. round(q.x) .. "\t" .. round(q.y))
                  end
                end
              end)
            end
          end
        end
      end
    end
  end

  for _, tag in pairs(force.find_chart_tags(surface)) do
    local t = { text = tag.text, x = round(tag.position.x), y = round(tag.position.y) }
    if tag.icon then t.icon = { type = tag.icon.type or "item", name = tag.icon.name } end
    if tag.last_user then t.user = tag.last_user.name end
    w:line("G\t" .. to_json(t))
  end
  for name, count in pairs(counted) do w:line("X\t" .. name .. "\t" .. count) end
  w:flush()
  return chunks
end

local function statistics(force, surface)
  local result = {}
  for _, kind in pairs({ "item", "fluid" }) do
    pcall(function()
      local stats = kind == "item" and force.get_item_production_statistics(surface) or force.get_fluid_production_statistics(surface)
      result[kind] = { produced = stats.input_counts, consumed = stats.output_counts }
    end)
  end
  return result
end

local function export()
  local force = game.forces["player"]
  local meta = {
    tick = game.tick, ticks_played = game.ticks_played, mods = script.active_mods,
    surfaces = {}, players = {}, forces = {},
  }

  local file_index = 0
  for _, surface in pairs(game.surfaces) do
    file_index = file_index + 1
    local chunks = export_surface(surface, force, file_index)
    local s = { file = "surface-" .. file_index .. ".txt", name = surface.name, index = surface.index, chunks = chunks }
    s.statistics = statistics(force, surface)
    pcall(function() s.evolution = game.forces["enemy"].get_evolution_factor(surface) end)
    meta.surfaces[#meta.surfaces + 1] = s
  end

  for _, player in pairs(game.players) do
    local p = {
      name = player.name, index = player.index, connected = player.connected, admin = player.admin,
      force = player.force.name, online_time = player.online_time,
    }
    pcall(function() p.surface = player.surface.index end)
    pcall(function() p.x, p.y = round(player.position.x), round(player.position.y) end)
    pcall(function() p.character = player.character ~= nil end)
    pcall(function() p.last_online = player.last_online end)
    pcall(function() if player.color then p.color = color_of(player.color) end end)
    meta.players[#meta.players + 1] = p
  end

  for _, f in pairs(game.forces) do
    local entry = { name = f.name, index = f.index }
    pcall(function() entry.rockets_launched = f.rockets_launched end)
    pcall(function()
      local researched, total = 0, 0
      for _, tech in pairs(f.technologies) do
        total = total + 1
        if tech.researched then researched = researched + 1 end
      end
      entry.researched, entry.technologies = researched, total
    end)
    pcall(function()
      if f.current_research then
        entry.research = f.current_research.name
        entry.research_progress = f.research_progress
      end
    end)
    meta.forces[#meta.forces + 1] = entry
  end

  write_file(OUT .. "meta.json", to_json(meta), false)
  write_file(OUT .. "done", "ok", false)
end

script.on_init(function()
  local ok, err = pcall(export)
  if not ok then write_file(OUT .. "error.txt", tostring(err), false) end
end)
