local baseUrl = "http://" .. ac.getServerIP() .. ":" .. ac.getServerPortHTTP() .. "/static/AutoModerationPlugin/"

local flags = {
    NO_LIGHTS = 1,
    NO_PARKING = 2,
    WRONG_WAY = 4
}

local flagImages = {
    [flags.NO_LIGHTS] = baseUrl .. "no_lights.png",
    [flags.NO_PARKING] = baseUrl .. "no_parking.png",
    [flags.WRONG_WAY] = baseUrl .. "wrong_way.png"
}

-- ASFALTO: until an image has loaded (or if it can't be loaded at all) the game draws a grey square. Draw a
-- warning badge instead, and the image only once it's ready.
local flagLabels = {
    [flags.NO_LIGHTS] = { 'LIGHTS ON', 'Turn your headlights on' },
    [flags.NO_PARKING] = { 'NO STOPPING', 'Keep moving: you are blocking the road' },
    [flags.WRONG_WAY] = { 'WRONG WAY', 'Turn around' }
}
local pathFlag = {}
for flag, imagePath in pairs(flagImages) do pathFlag[imagePath] = flag end

local flagsToDraw = {}
local autoModerationFlagEvent = ac.OnlineEvent({
    ac.StructItem.key("autoModerationFlag"),
    flags = ac.StructItem.byte()
}, function (sender, message)
    if sender ~= nil then return end

    flagsToDraw = {}

    for flag, imagePath in pairs(flagImages) do
        if bit.band(message.flags, flag) ~= 0 then
            table.insert(flagsToDraw, imagePath)
        end
    end
end)

local color = rgbm(255, 255, 255, 0.8)
local flagSize = vec2(128, 128)
local centerPos = nil
function script.drawUI()
    if #flagsToDraw > 0 then
        if centerPos == nil then
            centerPos = vec2(ac.getUI().windowSize.x / 2, 100)
        end

        local p1 = vec2(centerPos.x - (flagSize.x + 10) / 2 * #flagsToDraw, centerPos.y)

        for i, path in ipairs(flagsToDraw) do
            if ui.isImageReady(path) then
                ui.drawImage(path, p1, p1 + flagSize, color)
            else
                local label = flagLabels[pathFlag[path]] or { 'WARNING', '' }
                ui.drawRectFilled(p1, p1 + flagSize, rgbm(0.75, 0.08, 0.08, 0.9), 16)
                ui.drawRect(p1, p1 + flagSize, rgbm(1, 1, 1, 0.9), 16, nil, 3)
                ui.pushFont(ui.Font.Title)
                ui.drawTextClipped(label[1], p1 + vec2(6, 20), p1 + flagSize - vec2(6, 50), rgbm(1, 1, 1, 1), vec2(0.5, 0.5), true)
                ui.popFont()
                ui.drawTextClipped(label[2], p1 + vec2(8, 70), p1 + flagSize - vec2(8, 8), rgbm(1, 1, 1, 0.9), vec2(0.5, 0.5), true)
            end
            p1.x = p1.x + flagSize.x + 10
        end
    end
end
