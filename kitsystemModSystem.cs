using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Datastructures;
using System.Text;

namespace Kitsystem
{
    public class KitsystemMod : ModSystem
    {
        private ICoreServerAPI sapi;
        private KitData kits;
        private string configPath;
        private Dictionary<AssetLocation, ItemStack> itemPrototypes = new Dictionary<AssetLocation, ItemStack>();

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            configPath = Path.Combine(GamePaths.ModConfig, "kitsystem.json");
            LoadData();

            InitializeItemPrototypes();

            api.ChatCommands.Create("kit")
                .WithDescription("Kit system commands")
                .RequiresPrivilege(Privilege.chat)
                .BeginSubCommand("create")
                    .WithDescription("Create new kit (admin only)")
                    .RequiresPlayer()
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(
                        api.ChatCommands.Parsers.Word("kitName"),
                        api.ChatCommands.Parsers.OptionalInt("cooldownMinutes"),
                        api.ChatCommands.Parsers.OptionalWord("privileges")
                    )
                    .HandleWith(CreateKit)
                .EndSubCommand()
                .BeginSubCommand("delete")
                    .WithDescription("Delete kit (admin only)")
                    .RequiresPrivilege(Privilege.controlserver)
                    .WithArgs(api.ChatCommands.Parsers.Word("kitName"))
                    .HandleWith(DeleteKit)
                .EndSubCommand()
                .BeginSubCommand("list")
                    .WithDescription("List available kits")
                    .HandleWith(ListKits)
                .EndSubCommand()
                .BeginSubCommand("claim")
                    .WithDescription("Claim a kit")
                    .RequiresPlayer()
                    .WithArgs(api.ChatCommands.Parsers.Word("kitName"))
                    .HandleWith(ClaimKit)
                .EndSubCommand()
                .BeginSubCommand("info")
                    .WithDescription("Show detailed kit information")
                    .WithArgs(api.ChatCommands.Parsers.Word("kitName"))
                    .HandleWith(ShowKitInfo)
                .EndSubCommand()
                .BeginSubCommand("showplayerkits")
                    .WithDescription("Show player's kit status")
                    .RequiresPlayer()
                    .HandleWith(ShowPlayerKits)
                .EndSubCommand();
        }

        private TextCommandResult CreateKit(TextCommandCallingArgs args)
        {
            if (args.Caller.Player == null)
                return TextCommandResult.Error("Команду может использовать только игрок");

            if (args.Parsers == null || args.Parsers.Count < 1)
                return TextCommandResult.Error("Использование: /kit create <название> [время_восстановления=0] [привилегия]");

            string kitName = args.Parsers[0].GetValue() as string;
            
            if (string.IsNullOrEmpty(kitName)) 
                return TextCommandResult.Error("Название кита обязательно");
                
            kitName = kitName.ToLowerInvariant();
            if (kits.Kits.ContainsKey(kitName))
                return TextCommandResult.Error($"Кит '{kitName}' уже существует");

            var player = args.Caller.Player as IServerPlayer;
            if (player == null)
                return TextCommandResult.Error("Команду может использовать только игрок");

            var inventory = player.InventoryManager.GetHotbarInventory();
            if (inventory == null)
                return TextCommandResult.Error("Не удалось получить доступ к инвентарю игрока");

            // Получаем предметы из инвентаря с улучшенным анализом атрибутов
            var kitItems = LoadInventoryItemsForKit(inventory);
            if (kitItems.Count == 0)
                return TextCommandResult.Error("В панели быстрого доступа нет предметов. Добавьте предметы в панель быстрого доступа для создания кита.");

            // Получаем время перезарядки и определяем тип кита
            int cooldownMinutes = 0;
            if (args.Parsers.Count > 1 && args.Parsers[1].GetValue() != null)
            {
                cooldownMinutes = (args.Parsers[1].GetValue() as int?) ?? 0;
            }

            // Определяем тип кита: 0 = single (одноразовый), >0 = multi (многоразовый с кулдауном)
            string kitType = cooldownMinutes > 0 ? "multi" : "single";

            // Создаем новый кит
            var kit = new Kit
            {
                Type = kitType,
                Items = kitItems,
                CooldownMinutes = cooldownMinutes,
                RequiredPrivileges = new List<string>()
            };

            // Добавляем привилегии, если указаны
            string privilege = null;
            if (args.Parsers.Count > 2)
            {
                privilege = args.Parsers[2].GetValue() as string;
            }
            
            if (!string.IsNullOrEmpty(privilege))
            {
                kit.RequiredPrivileges.Add(privilege);
            }

            kits.Kits[kitName] = kit;
            SaveData();
            
            // Формируем подробное сообщение о создании кита
            var sb = new StringBuilder();
            sb.AppendLine($"=== Кит '{kitName}' создан! ===");
            sb.AppendLine($"Тип: {(kitType == "multi" ? "Многоразовый" : "Одноразовый")}");
            
            if (kitType == "multi") 
            {
                sb.AppendLine($"Кулдаун: {FormatTimeRemaining(TimeSpan.FromMinutes(cooldownMinutes))}");
            }
            
            if (kit.RequiredPrivileges.Count > 0)
            {
                sb.AppendLine($"Требуемые привилегии: {string.Join(", ", kit.RequiredPrivileges)}");
            }
            else
            {
                sb.AppendLine("Требуемые привилегии: Нет");
            }
            
            sb.AppendLine("\nПредметы в ките:");
            
            foreach (var item in kitItems)
            {
                string itemName = "Неизвестный предмет";
                string itemCategory = "";
                string itemQuality = "";
                
                try
                {
                    var assetLocation = new AssetLocation(item.Code);
                    var itemObj = sapi.World.GetItem(assetLocation) as CollectibleObject;
                    var blockObj = itemObj == null ? sapi.World.GetBlock(assetLocation) as CollectibleObject : null;
                    
                    if (itemObj != null || blockObj != null)
                    {
                        var collectible = itemObj ?? blockObj;
                        ItemStack tempStack = new ItemStack(collectible, item.StackSize);
                        
                        // Если у предмета есть атрибуты, применяем их
                        if (!string.IsNullOrEmpty(item.Material))
                        {
                            ProcessItemAttributes(tempStack, item.Material);
                            
                            // Для пирогов и еды нужна дополнительная обработка
                            if (NeedsSpecialInitialization(tempStack.Collectible.Code.Path))
                            {
                                tempStack = FinalizeItemStack(tempStack);
                            }
                        }
                        
                        // Получаем имя и категорию предмета
                        itemName = tempStack.GetName();
                        itemCategory = GetItemCategory(tempStack);
                        itemQuality = GetItemQuality(tempStack);
                    }
                }
                catch (Exception ex)
                {
                    // В случае ошибки используем код предмета
                    itemName = item.Code;
                    sapi.Logger.Warning($"Ошибка при отображении предмета {item.Code}: {ex.Message}");
                }
                
                // Формируем полное описание предмета
                string itemDescription = $"{item.StackSize}x {itemName}";
                if (!string.IsNullOrEmpty(itemCategory))
                {
                    itemDescription += $" ({itemCategory}";
                    if (!string.IsNullOrEmpty(itemQuality))
                    {
                        itemDescription += $", {itemQuality}";
                    }
                    itemDescription += ")";
                }
                else if (!string.IsNullOrEmpty(itemQuality))
                {
                    itemDescription += $" ({itemQuality})";
                }
                
                sb.AppendLine($"- {itemDescription}");
            }

            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult DeleteKit(TextCommandCallingArgs args)
        {
            if (args.Parsers == null || args.Parsers.Count < 1) 
                return TextCommandResult.Error("Использование: /kit delete <название>");
                
            string kitName = args.Parsers[0].GetValue() as string;
            if (string.IsNullOrEmpty(kitName))
                return TextCommandResult.Error("Название кита обязательно");
                
            kitName = kitName.ToLowerInvariant();
            
            if (!kits.Kits.ContainsKey(kitName))
                return TextCommandResult.Error($"Кит '{kitName}' не найден!");

            // Удаляем информацию о ките у всех игроков
            foreach (var playerData in kits.PlayerData.Values)
            {
                playerData.ClaimedSingleKits.Remove(kitName);
                playerData.MultiKitCooldowns.Remove(kitName);
            }

            kits.Kits.Remove(kitName);
            SaveData();
            
            return TextCommandResult.Success($"Кит '{kitName}' успешно удален! Все данные игроков, связанные с этим китом, очищены.");
        }

        private TextCommandResult ListKits(TextCommandCallingArgs args)
        {
            if (kits.Kits.Count == 0)
                return TextCommandResult.Success("Доступных китов нет.");

            var sb = new StringBuilder();
            sb.AppendLine("=== Доступные киты ===");

            // Группируем киты по типу (single/multi)
            var singleKits = kits.Kits.Where(k => k.Value.Type == "single").ToList();
            var multiKits = kits.Kits.Where(k => k.Value.Type == "multi").ToList();

            if (singleKits.Any())
            {
                sb.AppendLine("\n== Одноразовые киты ==");
                foreach (var kit in singleKits.OrderBy(k => k.Key))
                {
                string privileges = kit.Value.RequiredPrivileges.Count > 0 
                        ? $" (Требуется: {string.Join(", ", kit.Value.RequiredPrivileges)})"
                        : "";
                        
                    sb.AppendLine($"- {kit.Key}: {kit.Value.Items.Count} предм.{privileges}");
                }
            }

            if (multiKits.Any())
            {
                sb.AppendLine("\n== Многоразовые киты ==");
                foreach (var kit in multiKits.OrderBy(k => k.Key))
                {
                    TimeSpan cooldown = TimeSpan.FromMinutes(kit.Value.CooldownMinutes);
                    string formattedCooldown = FormatTimeRemaining(cooldown);
                    
                    string privileges = kit.Value.RequiredPrivileges.Count > 0
                        ? $" (Требуется: {string.Join(", ", kit.Value.RequiredPrivileges)})"
                        : "";
                        
                    sb.AppendLine($"- {kit.Key}: {kit.Value.Items.Count} предм., Кулдаун: {formattedCooldown}{privileges}");
                }
            }
            
            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult ClaimKit(TextCommandCallingArgs args)
        {
            if (args.Parsers == null || args.Parsers.Count < 1)
                return TextCommandResult.Error("Использование: /kit claim <название>");
                
            string kitName = args.Parsers[0].GetValue() as string;
            if (string.IsNullOrEmpty(kitName))
                return TextCommandResult.Error("Название кита обязательно");
                
            kitName = kitName.ToLowerInvariant();
            
            if (!kits.Kits.ContainsKey(kitName))
                return TextCommandResult.Error($"Кит '{kitName}' не найден!");

            var kit = kits.Kits[kitName];
            var player = args.Caller.Player as IServerPlayer;
            if (player == null)
                return TextCommandResult.Error("Команду может использовать только игрок");

            // Проверка привилегий
            bool isAdmin = player.HasPrivilege(Privilege.controlserver);
            if (!isAdmin && !player.HasPrivilege(Privilege.chat))
            {
                return TextCommandResult.Error("Вам нужна привилегия 'chat' для получения китов");
            }
            
            if (!isAdmin)
            {
                foreach (var privilege in kit.RequiredPrivileges)
                {
                    if (!player.HasPrivilege(privilege))
                    {
                        return TextCommandResult.Error($"Вам нужна привилегия '{privilege}' для получения этого кита");
                    }
                }
            }

            // Получаем или создаем данные игрока
            if (!kits.PlayerData.TryGetValue(player.PlayerUID, out var playerData))
            {
                playerData = new PlayerKits();
                kits.PlayerData[player.PlayerUID] = playerData;
            }

            // Проверка возможности получения кита
            if (kit.Type == "single")
            {
                if (playerData.ClaimedSingleKits.Contains(kitName) && !isAdmin)
                {
                    return TextCommandResult.Error("Вы уже получили этот одноразовый кит");
                }
            }
            else // multi
            {
                if (playerData.MultiKitCooldowns.TryGetValue(kitName, out var cooldown) && !isAdmin)
                {
                    long currentTime = sapi.World.ElapsedMilliseconds;
                    if (cooldown.NextAvailableTime > currentTime)
                    {
                        TimeSpan remainingTime = TimeSpan.FromMilliseconds(cooldown.NextAvailableTime - currentTime);
                        return TextCommandResult.Error($"Вы должны подождать {FormatTimeRemaining(remainingTime)} перед получением этого кита снова");
                    }
                }
            }
            
            // Проверка достаточно ли места в инвентаре
            if (!HasEnoughInventorySpace(player, kit.Items))
            {
                return TextCommandResult.Error("У вас недостаточно места в инвентаре для этого кита. Пожалуйста, освободите место.");
            }

            // Выдача предметов
            var result = GiveItems(player, kit);

            // Обновление информации о получении
            if (!isAdmin)
            {
                if (kit.Type == "single")
                {
                    playerData.ClaimedSingleKits.Add(kitName);
                }
                else // multi
                {
                    long currentTime = sapi.World.ElapsedMilliseconds;
                    long nextAvailable = currentTime + (long)(TimeSpan.FromMinutes(kit.CooldownMinutes).TotalMilliseconds);
                    playerData.MultiKitCooldowns[kitName] = new KitCooldown
                    {
                        LastClaimTime = currentTime,
                        NextAvailableTime = nextAvailable
                    };
                }
            }

            SaveData();
            
            // Добавляем заголовок к отчету о полученных предметах
            var finalMsg = $"=== Получен кит: {kitName} ===\n" + result.ItemsReport;
            
            return TextCommandResult.Success(finalMsg);
        }

        private TextCommandResult ShowKitInfo(TextCommandCallingArgs args)
        {
            if (args.Parsers == null || args.Parsers.Count < 1)
                return TextCommandResult.Error("Использование: /kit info <название>");
                
            string kitName = args.Parsers[0].GetValue() as string;
            if (string.IsNullOrEmpty(kitName))
                return TextCommandResult.Error("Название кита обязательно");
                
            kitName = kitName.ToLowerInvariant();
            
            if (!kits.Kits.ContainsKey(kitName))
                return TextCommandResult.Error($"Кит '{kitName}' не найден!");

            var kit = kits.Kits[kitName];
            var sb = new StringBuilder();
            
            sb.AppendLine($"=== Кит: {kitName} ===");
            sb.AppendLine($"Тип: {(kit.Type == "multi" ? "Многоразовый" : "Одноразовый")}");
            
            // Кулдаун для мульти-китов
            if (kit.Type == "multi")
            {
                TimeSpan cooldown = TimeSpan.FromMinutes(kit.CooldownMinutes);
                sb.AppendLine($"Кулдаун: {FormatTimeRemaining(cooldown)}");
                
                // Проверка информации о кулдауне для текущего игрока
                var player = args.Caller.Player as IServerPlayer;
                if (player != null)
                {
                    if (kits.PlayerData.TryGetValue(player.PlayerUID, out var playerData))
                    {
                        if (playerData.MultiKitCooldowns.TryGetValue(kitName, out var kitCooldown))
                        {
                            long currentTime = sapi.World.ElapsedMilliseconds;
                            if (kitCooldown.NextAvailableTime > currentTime)
                            {
                                TimeSpan remainingTime = TimeSpan.FromMilliseconds(kitCooldown.NextAvailableTime - currentTime);
                                sb.AppendLine($"Доступен через: {FormatTimeRemaining(remainingTime)}");
                            }
                            else
                            {
                                sb.AppendLine("Статус: Доступен сейчас");
                            }
                        }
                        else
                        {
                            sb.AppendLine("Статус: Доступен сейчас (никогда не получен)");
                        }
                    }
                    else
                    {
                        sb.AppendLine("Статус: Доступен сейчас (никогда не получен)");
                    }
                }
            }
            else if (kit.Type == "single")
            {
                // Проверка статуса для одноразовых китов
                var player = args.Caller.Player as IServerPlayer;
                if (player != null)
                {
                    if (kits.PlayerData.TryGetValue(player.PlayerUID, out var playerData))
                    {
                        bool claimed = playerData.ClaimedSingleKits.Contains(kitName);
                        sb.AppendLine($"Статус: {(claimed ? "Уже получен" : "Доступен")}");
                    }
                    else
                    {
                        sb.AppendLine("Статус: Доступен (никогда не получен)");
                    }
                }
            }
            
            // Привилегии
            string privileges = kit.RequiredPrivileges.Count > 0 
                ? string.Join(", ", kit.RequiredPrivileges)
                : "Нет";
            sb.AppendLine($"Требуемые привилегии: {privileges}");
            
            // Проверка достаточно места в инвентаре
            var requiredSlots = kit.Items.Count;
            var player2 = args.Caller.Player as IServerPlayer;
            if (player2 != null)
            {
                bool hasSpace = HasEnoughInventorySpace(player2, kit.Items);
                sb.AppendLine($"Место в инвентаре: {(hasSpace ? "Достаточно" : "Недостаточно места!")}");
            }
            
            // Список предметов
            sb.AppendLine("\nПредметы:");
            foreach (var item in kit.Items)
            {
                string itemName = "Неизвестный предмет";
                string itemCategory = "";
                string itemQuality = "";
                
                try
                {
                    var assetLocation = new AssetLocation(item.Code);
                    var itemObj = sapi.World.GetItem(assetLocation) as CollectibleObject;
                    var blockObj = itemObj == null ? sapi.World.GetBlock(assetLocation) as CollectibleObject : null;

                    if (itemObj != null || blockObj != null)
                    {
                        var collectible = itemObj ?? blockObj;
                        
                        // Создаем временный стек предмета с атрибутами
                        ItemStack tempStack = new ItemStack(collectible, item.StackSize);
                        
                        // Если у предмета есть атрибуты, применяем их
                        if (!string.IsNullOrEmpty(item.Material))
                        {
                            ProcessItemAttributes(tempStack, item.Material);
                            
                            // Для пирогов и еды нужна дополнительная обработка
                            if (NeedsSpecialInitialization(tempStack.Collectible.Code.Path))
                            {
                                tempStack = FinalizeItemStack(tempStack);
                            }
                        }
                        
                        // Получаем имя и категорию предмета
                        itemName = tempStack.GetName();
                        itemCategory = GetItemCategory(tempStack);
                        itemQuality = GetItemQuality(tempStack);
                    }
                }
                catch (Exception ex)
                {
                    itemName = item.Code;
                    sapi.Logger.Warning($"Ошибка при отображении предмета {item.Code}: {ex.Message}");
                }
                
                // Формируем полное описание предмета
                string itemDescription = $"{item.StackSize}x {itemName}";
                if (!string.IsNullOrEmpty(itemCategory))
                {
                    itemDescription += $" ({itemCategory}";
                    if (!string.IsNullOrEmpty(itemQuality))
                    {
                        itemDescription += $", {itemQuality}";
                    }
                    itemDescription += ")";
                }
                else if (!string.IsNullOrEmpty(itemQuality))
                {
                    itemDescription += $" ({itemQuality})";
                }
                
                sb.AppendLine($"- {itemDescription}");
            }

            return TextCommandResult.Success(sb.ToString());
        }

        private TextCommandResult ShowPlayerKits(TextCommandCallingArgs args)
        {
            var player = args.Caller.Player as IServerPlayer;
            if (player == null)
                return TextCommandResult.Error("Команду может использовать только игрок");

            var sb = new StringBuilder();
            sb.AppendLine($"=== Статус китов для {player.PlayerName} ===");

            if (!kits.PlayerData.TryGetValue(player.PlayerUID, out var playerData))
            {
                playerData = new PlayerKits();
                kits.PlayerData[player.PlayerUID] = playerData;
            }

            bool hasAdmin = player.HasPrivilege(Privilege.controlserver);

            // Создадим списки для разных категорий китов
            var availableSingleKits = new List<string>();
            var claimedSingleKits = new List<string>();
            var availableMultiKits = new List<string>();
            var onCooldownMultiKits = new List<Tuple<string, TimeSpan>>();
            var unavailableKits = new List<string>(); // Киты, недоступные из-за отсутствия привилегий

            foreach (var kitEntry in kits.Kits)
            {
                string kitName = kitEntry.Key;
                Kit kit = kitEntry.Value;
                
                // Проверка привилегий
                bool hasAccess = hasAdmin;
                if (!hasAccess && kit.RequiredPrivileges.Count == 0)
                {
                    hasAccess = true;
                }
                else if (!hasAccess)
                {
                    hasAccess = kit.RequiredPrivileges.All(p => player.HasPrivilege(p));
                }

                if (!hasAccess)
                {
                    unavailableKits.Add(kitName);
                    continue;
                }

                // Категоризация китов
                if (kit.Type == "single")
                {
                    if (playerData.ClaimedSingleKits.Contains(kitName))
                    {
                        claimedSingleKits.Add(kitName);
                }
                else
                {
                        availableSingleKits.Add(kitName);
                    }
                }
                else // multi
                {
                    if (playerData.MultiKitCooldowns.TryGetValue(kitName, out var cooldown))
                    {
                        long currentTime = sapi.World.ElapsedMilliseconds;
                        if (cooldown.NextAvailableTime > currentTime)
                        {
                            TimeSpan remainingTime = TimeSpan.FromMilliseconds(cooldown.NextAvailableTime - currentTime);
                            onCooldownMultiKits.Add(new Tuple<string, TimeSpan>(kitName, remainingTime));
                        }
                        else
                        {
                            availableMultiKits.Add(kitName);
                        }
                    }
                    else
                    {
                        availableMultiKits.Add(kitName);
                    }
                }
            }

            // Вывод информации о доступных одноразовых китах
            if (availableSingleKits.Count > 0)
            {
                sb.AppendLine("\n== Доступные одноразовые киты ==");
                foreach (var kitName in availableSingleKits.OrderBy(k => k))
                {
                    sb.AppendLine($"- {kitName}");
                }
            }

            // Вывод информации о полученных одноразовых китах
            if (claimedSingleKits.Count > 0)
            {
                sb.AppendLine("\n== Полученные одноразовые киты ==");
                foreach (var kitName in claimedSingleKits.OrderBy(k => k))
                {
                    sb.AppendLine($"- {kitName}");
                }
            }

            // Вывод информации о доступных многоразовых китах
            if (availableMultiKits.Count > 0)
            {
                sb.AppendLine("\n== Доступные многоразовые киты ==");
                foreach (var kitName in availableMultiKits.OrderBy(k => k))
                {
                    sb.AppendLine($"- {kitName}");
                }
            }

            // Вывод информации о многоразовых китах на перезарядке
            if (onCooldownMultiKits.Count > 0)
            {
                sb.AppendLine("\n== Многоразовые киты на перезарядке ==");
                foreach (var kitInfo in onCooldownMultiKits.OrderBy(k => k.Item2))
                {
                    string kitName = kitInfo.Item1;
                    TimeSpan remainingTime = kitInfo.Item2;
                    sb.AppendLine($"- {kitName}: Доступен через {FormatTimeRemaining(remainingTime)}");
                }
            }

            // Вывод информации о недоступных китах
            if (unavailableKits.Count > 0)
            {
                sb.AppendLine("\n== Недоступные киты (отсутствуют привилегии) ==");
                foreach (var kitName in unavailableKits.OrderBy(k => k))
                {
                    var kit = kits.Kits[kitName];
                    string requiredPrivileges = string.Join(", ", kit.RequiredPrivileges);
                    sb.AppendLine($"- {kitName}: Требуется {requiredPrivileges}");
                }
            }

            if (availableSingleKits.Count == 0 && claimedSingleKits.Count == 0 && 
                availableMultiKits.Count == 0 && onCooldownMultiKits.Count == 0 && 
                unavailableKits.Count == 0)
            {
                sb.AppendLine("\nНа сервере нет доступных китов.");
            }

            return TextCommandResult.Success(sb.ToString());
        }

        private class KitClaimResult
        {
            public string ItemsReport { get; set; }
            public List<string> FailedItems { get; set; } = new List<string>();
        }

        private KitClaimResult GiveItems(IServerPlayer player, Kit kit)
        {
            var result = new KitClaimResult();
            var successfulItems = new List<string>();
            var pos = player.Entity.Pos.XYZ.AddCopy(0, 0.5, 0);

            foreach (var item in kit.Items)
            {
                try
                {
                    string itemCode = item.Code.Contains(":") ? item.Code : "game:" + item.Code;
                    
                    // Создаем ItemStack с атрибутами
                    ItemStack stack = CreateItemWithAttributes(itemCode, item.StackSize, item.Material);
                    
                    if (stack != null)
                    {
                        string itemType = GetItemCategory(stack);
                        bool isFood = IsFood(stack.Collectible.Code.Path);
                        
                        // ВАЖНО: Для еды создаем НОВЫЙ объект с сегодняшней датой
                        if (isFood)
                        {
                            // Создаем новый свежий предмет еды
                            ItemStack freshFoodItem = new ItemStack(stack.Collectible, stack.StackSize);
                            
                            // Копируем не связанные со временем атрибуты (если они есть)
                            if (stack.Attributes != null)
                            {
                                // Получаем список атрибутов
                                var attributeNames = new List<string>();
                                foreach (var entry in stack.Attributes)
                                {
                                    attributeNames.Add(entry.Key);
                                }
                                
                                foreach (var attribute in attributeNames)
                                {
                                    // Пропускаем атрибуты, связанные со временем
                                    if (!attribute.Contains("time") && 
                                        !attribute.Contains("Time") && 
                                        !attribute.Contains("transition") && 
                                        !attribute.Contains("perish") &&
                                        !attribute.Contains("spoil"))
                                    {
                                        // Копируем атрибут в новый предмет
                                        freshFoodItem.Attributes[attribute] = stack.Attributes[attribute].Clone();
                                    }
                                }
                            }
                            
                            // Применяем полную инициализацию свежести
                            InitializeFoodFreshness(freshFoodItem);
                            
                            // Используем свежий предмет вместо оригинального
                            stack = freshFoodItem;
                        }
                        else
                        {
                            // Для не-еды применяем финальные настройки (ведра и т.д.)
                            stack = FinalizeItemStack(stack);
                        }
                        
                        // Отображаем в консоли атрибуты для отладки
                        if (stack.Attributes != null && stack.Attributes.Count > 0)
                        {
                            LogItemAttributes(stack, $"Атрибуты {itemCode} перед выдачей");
                        }
                        
                        // Спавним предмет в мире
                        sapi.World.SpawnItemEntity(stack, pos);
                        
                        // Добавляем информацию о выданном предмете
                        string itemName = stack.GetName();
                        string itemQuality = GetItemQuality(stack);
                        
                        string itemDescription = $"{item.StackSize}x {itemName}";
                        if (!string.IsNullOrEmpty(itemType))
                        {
                            itemDescription += $" ({itemType}";
                            if (!string.IsNullOrEmpty(itemQuality))
                            {
                                itemDescription += $", {itemQuality}";
                            }
                            itemDescription += ")";
                        }
                        else if (!string.IsNullOrEmpty(itemQuality))
                        {
                            itemDescription += $" ({itemQuality})";
                        }
                        
                        successfulItems.Add(itemDescription);
                    }
                    else
                    {
                        result.FailedItems.Add($"{item.StackSize}x {itemCode} (не найден)");
                    }
                }
                catch (Exception ex)
                {
                    sapi.Logger.Error($"Ошибка при выдаче предмета {item.Code}: {ex.Message}");
                    result.FailedItems.Add($"{item.StackSize}x {item.Code} (ошибка: {ex.Message})");
                }
            }

            result.ItemsReport = "\nПолученные предметы:\n" + 
                string.Join("\n", successfulItems.Select(item => $"- {item}"));
            
            if (result.FailedItems.Any())
            {
                result.ItemsReport += "\n\nНеудачные предметы:\n" + 
                    string.Join("\n", result.FailedItems.Select(item => $"- {item}"));
            }
            
            return result;
        }

        private ItemStack CreateItemWithAttributes(string itemCode, int stackSize, string attributesData)
        {
            ItemStack stack = null;
            AssetLocation assetLocation = new AssetLocation(itemCode);
            
            try
            {
                // Пытаемся получить прототип предмета
                if (itemPrototypes.TryGetValue(assetLocation, out ItemStack prototype))
                {
                    // Клонируем прототип для создания нового экземпляра с правильными атрибутами
                    stack = prototype.Clone();
                    stack.StackSize = stackSize;
                }
                else
                {
                    // Если прототип не найден, создаем предмет стандартным способом
                    var itemObj = sapi.World.GetItem(assetLocation);
                    var blockObj = itemObj == null ? sapi.World.GetBlock(assetLocation) : null;
                    
                    if (itemObj != null)
                    {
                        stack = new ItemStack(itemObj, stackSize);
                    }
                    else if (blockObj != null)
                    {
                        stack = new ItemStack(blockObj, stackSize);
                    }
                }
                
                // Если стек создан, применяем атрибуты из сохраненных данных
                if (stack != null && !string.IsNullOrEmpty(attributesData))
                {
                    ProcessItemAttributes(stack, attributesData);
                }
                
                return stack;
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"Ошибка при создании предмета {itemCode}: {ex.Message}");
                return null;
            }
        }

        // Определение категории предмета
        private string GetItemCategory(ItemStack stack)
        {
            string code = stack.Collectible.Code.Path;
            
            if (IsFood(code))
                return "Еда";
                
            if (code.Contains("bucket") && stack.Attributes?.HasAttribute("liquid") == true)
                return "Контейнер с жидкостью";
                
            if (code.Contains("tool"))
                return "Инструмент";
                
            if (code.Contains("weapon") || code.Contains("sword") || code.Contains("axe") || code.Contains("spear"))
                return "Оружие";
                
            if (code.Contains("armor") || code.Contains("helmet") || code.Contains("boots") || code.Contains("leggings") || code.Contains("chestplate"))
                return "Броня";
                
            return "";
        }

        // Определение качества предмета
        private string GetItemQuality(ItemStack stack)
        {
            // Для еды не показываем информацию о свежести
            if (IsFood(stack.Collectible.Code.Path))
            {
                return ""; // Удаляем все упоминания о свежести еды
            }
            
            // Проверка дополнительных атрибутов для других типов предметов
            if (stack.Attributes != null)
            {
                if (stack.Collectible.Code.Path.Contains("pie"))
                {
                    if (stack.Collectible.Code.Path.Contains("perfect"))
                        return "Идеально пропечённый";
                    if (stack.Collectible.Code.Path.Contains("raw"))
                        return "Сырой";
                    if (stack.Collectible.Code.Path.Contains("partbaked"))
                        return "Недопечённый";
                    if (stack.Collectible.Code.Path.Contains("charred"))
                        return "Подгоревший";
                }
            }
            
            return "";
        }
        
        private void LogItemAttributes(ItemStack stack, string prefix)
        {
            try
            {
                sapi.Logger.Debug($"{prefix}: {stack.Collectible.Code}");
                
                if (stack.Attributes == null)
                {
                    sapi.Logger.Debug("  Атрибуты: отсутствуют");
                    return;
                }
                
                // Получаем все ключи из TreeAttribute
                var treeAttr = stack.Attributes as TreeAttribute;
                if (treeAttr != null)
                {
                    foreach (var key in treeAttr.Keys)
                    {
                        var attr = treeAttr[key];
                        string attrType = attr.GetType().Name;
                        string attrValue = "сложный тип";
                        
                        if (attr is StringAttribute)
                            attrValue = ((StringAttribute)attr).value;
                        else if (attr is IntAttribute)
                            attrValue = ((IntAttribute)attr).value.ToString();
                        else if (attr is FloatAttribute)
                            attrValue = ((FloatAttribute)attr).value.ToString();
                        else if (attr is BoolAttribute)
                            attrValue = ((BoolAttribute)attr).value.ToString();
                        
                        sapi.Logger.Debug($"  {key} ({attrType}): {attrValue}");
                    }
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"Ошибка при логировании атрибутов предмета: {ex.Message}");
            }
        }
        
        // Проверка, нуждается ли предмет в специальной инициализации
        private bool NeedsSpecialInitialization(string code)
        {
            return code.Contains("bucket") || 
                   IsFood(code);
        }
        
        // Проверка, является ли предмет едой
        private bool IsFood(string code)
        {
            return code.StartsWith("food") || 
                   code.Contains("fruit") || 
                   code.Contains("vegetable") || 
                   code.Contains("grain") || 
                   code.Contains("meat") || 
                   code.Contains("fish") || 
                   code.Contains("pie") || 
                   code.Contains("cheese") || 
                   code.Contains("butter") || 
                   code.Contains("milk") || 
                   code.Contains("egg") || 
                   code.Contains("dough") || 
                   code.Contains("curd") || 
                   code.Contains("portion");
        }
        
        // Метод для финальной инициализации предмета перед выдачей
        private ItemStack FinalizeItemStack(ItemStack stack)
        {
            try
            {
                string code = stack.Collectible.Code.Path;
                
                // Проверяем, нужна ли специальная инициализация для этого типа предмета
                if (NeedsSpecialInitialization(code))
                {
                    // Клонируем стек для безопасности
                    ItemStack finalStack = stack.Clone();
                    
                    // Для ведер иногда нужно создать ucontents из liquid
                    if (code.Contains("bucket") && finalStack.Attributes != null)
                    {
                        if (!finalStack.Attributes.HasAttribute("ucontents") && 
                            finalStack.Attributes.HasAttribute("liquid"))
                        {
                            string liquidCode = finalStack.Attributes.GetString("liquid");
                            if (!string.IsNullOrEmpty(liquidCode))
                            {
                                InitializeBucketContents(finalStack, liquidCode);
                            }
                        }
                    }
                    
                    // Инициализация для еды
                    if (IsFood(code))
                    {
                        InitializeFoodFreshness(finalStack);
                    }
                    
                    return finalStack;
                }
                
                return stack;
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"Ошибка при финализации предмета {stack.Collectible.Code}: {ex.Message}");
                return stack;
            }
        }
        
        // Инициализация содержимого ведра
        private void InitializeBucketContents(ItemStack bucketStack, string liquidCode)
        {
            // Создаем дерево атрибутов для содержимого
            var ucontents = new TreeAttribute();
            
            // Создаем атрибут для жидкости
            var liquidAttr = new TreeAttribute();
            liquidAttr.SetString("type", "item");
            liquidAttr.SetString("code", liquidCode + "portion");
            liquidAttr.SetBool("makefull", true);
            
            // Добавляем в содержимое
            ucontents["0"] = liquidAttr;
            
            // Устанавливаем содержимое ведра
            bucketStack.Attributes["ucontents"] = ucontents;
            
            // Устанавливаем флаг заполненности
            bucketStack.Attributes.SetFloat("fillLevel", 1.0f);
        }

        // Инициализация атрибутов свежести для еды
        private void InitializeFoodFreshness(ItemStack foodStack)
        {
            try
            {
                if (foodStack.Attributes == null)
                {
                    foodStack.Attributes = new TreeAttribute();
                }
                
                // КРИТИЧЕСКИ ВАЖНО: Полностью удаляем все атрибуты еды и создаем новые
                // Это предотвращает проблемы с игровой логикой обновления времени
                ITreeAttribute freshAttributes = new TreeAttribute();
                
                // Копируем неважные для времени атрибуты (например цвет)
                if (foodStack.Attributes != null)
                {
                    var attributeNames = new List<string>();
                    foreach (var entry in foodStack.Attributes)
                    {
                        attributeNames.Add(entry.Key);
                    }
                    
                    foreach (var attribute in attributeNames)
                    {
                        // Пропускаем все атрибуты, связанные со временем
                        if (!attribute.Contains("time") && 
                            !attribute.Contains("Time") && 
                            !attribute.Contains("transition") && 
                            !attribute.Contains("perish") &&
                            !attribute.Contains("spoil") &&
                            !attribute.Contains("created"))
                        {
                            // Копируем атрибут в свежие атрибуты
                            freshAttributes[attribute] = foodStack.Attributes[attribute].Clone();
                        }
                    }
                }
                
                // Заменяем все атрибуты новыми
                foodStack.Attributes = freshAttributes;
                
                // ПРИНУДИТЕЛЬНОЕ СОЗДАНИЕ ЕДЫ КАК СВЕЖЕЙ
                // Устанавливаем текущую дату игры как время создания
                long worldTime = (long)sapi.World.Calendar.TotalDays;
                long currentUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                
                // Сначала получаем свойства порчи для этого типа еды
                var props = foodStack.Collectible.GetTransitionableProperties(sapi.World, foodStack, null);
                
                if (props != null && props.Length > 0)
                {
                    // Находим свойство порчи
                    foreach (var prop in props)
                    {
                        if (prop.Type == EnumTransitionType.Perish)
                        {
                            // 1. Устанавливаем время перехода в 0 (совершенно свежая еда)
                            foodStack.Attributes.SetFloat("transitionedTime", 0f);
                            
                            // 2. Устанавливаем текущее время как время создания еды
                            foodStack.Attributes.SetLong("madeUnixTime", currentUnixTimeMs);
                            
                            // 3. Устанавливаем максимальное время свежести для этого типа еды
                            if (prop.FreshHours != null)
                            {
                                foodStack.Attributes.SetFloat("freshHours", prop.FreshHours.avg);
                            }
                            
                            // 4. Устанавливаем время перехода для этого типа еды
                            if (prop.TransitionHours != null)
                            {
                                foodStack.Attributes.SetFloat("transitionHours", prop.TransitionHours.avg);
                            }
                            
                            // 5. Устанавливаем статус как "не испорченный"
                            foodStack.Attributes.SetInt("perished", 0);
                            
                            break; // Обрабатываем только первое свойство порчи
                        }
                    }
                }
                
                // Если не удалось получить значения из свойств, устанавливаем разумные значения по умолчанию
                if (!foodStack.Attributes.HasAttribute("freshHours"))
                {
                    foodStack.Attributes.SetFloat("freshHours", 72f); // 3 дня по умолчанию
                }
                
                if (!foodStack.Attributes.HasAttribute("transitionHours"))
                {
                    foodStack.Attributes.SetFloat("transitionHours", 24f); // 1 день на переход по умолчанию
                }
                
                // Устанавливаем критически важные атрибуты времени напрямую
                foodStack.Attributes.SetLong("lastTickTime", sapi.World.ElapsedMilliseconds);
                
                // Для работы в предметах пирога и стеков еды
                if (foodStack.Collectible.Code.Path.Contains("pie"))
                {
                    foodStack.Attributes.SetFloat("timeTillExpiry", 999999f); // Очень большой срок годности
                }
                
                sapi.Logger.Debug($"Еда {foodStack.Collectible.Code} успешно инициализирована как свежая");
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"Ошибка при инициализации свежести еды: {ex.Message}");
            }
        }

        private bool HasEnoughInventorySpace(IServerPlayer player, List<KitItem> items)
        {
            int requiredSlots = items.Count;
            int freeSlots = 0;

            var hotbar = player.InventoryManager.GetHotbarInventory();
            if (hotbar != null)
            {
                for (int i = 0; i < hotbar.Count; i++)
                {
                    if (hotbar[i].Empty) freeSlots++;
                }
            }

            var mainInv = player.InventoryManager.GetOwnInventory(GlobalConstants.backpackInvClassName);
            if (mainInv != null)
            {
                for (int i = 0; i < mainInv.Count; i++)
                {
                    if (mainInv[i].Empty) freeSlots++;
                }
            }

            return freeSlots >= requiredSlots;
        }

        private string ExtractItemAttributes(ItemStack stack)
        {
            if (stack.Attributes == null || stack.Attributes.Count == 0)
                return null;
            
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    BinaryWriter writer = new BinaryWriter(ms);
                    stack.Attributes.ToBytes(writer);
                    
                    byte[] attrBytes = ms.ToArray();
                    string base64Attrs = Convert.ToBase64String(attrBytes);
                    
                    return "FULLATTR:" + base64Attrs;
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"Error serializing attributes for {stack.Collectible.Code}: {ex.Message}");
                
                try
                {
                    List<string> attributes = new List<string>();
                    
                    foreach (var key in (stack.Attributes as TreeAttribute)?.Keys ?? Enumerable.Empty<string>())
                    {
                        var attr = stack.Attributes[key];
                        
                        if (attr is StringAttribute stringAttr)
                            attributes.Add($"{key}:string:{stringAttr.value}");
                        else if (attr is IntAttribute intAttr)
                            attributes.Add($"{key}:int:{intAttr.value}");
                        else if (attr is FloatAttribute floatAttr)
                            attributes.Add($"{key}:float:{floatAttr.value}");
                        else if (attr is BoolAttribute boolAttr)
                            attributes.Add($"{key}:bool:{boolAttr.value}");
                        else if (attr is TreeAttribute)
                            attributes.Add($"{key}:tree:complex");
                    }
                    
                    if (attributes.Count > 0)
                        return "BASICATTR:" + string.Join("|", attributes);
                }
                catch (Exception innerEx)
                {
                    sapi.Logger.Error($"Error creating basic attributes for {stack.Collectible.Code}: {innerEx.Message}");
                }
                
                return null;
            }
        }

        private void ProcessItemAttributes(ItemStack stack, string materialData)
        {
            if (string.IsNullOrEmpty(materialData))
                return;
                
            try
            {
                if (materialData.StartsWith("FULLATTR:"))
                {
                    string base64Attrs = materialData.Substring(9);
                    byte[] attrBytes = Convert.FromBase64String(base64Attrs);
                    
                    using (MemoryStream ms = new MemoryStream(attrBytes))
                    {
                        BinaryReader reader = new BinaryReader(ms);
                        stack.Attributes = new TreeAttribute();
                        stack.Attributes.FromBytes(reader);
                    }
                    
                    sapi.Logger.Debug($"Successfully processed full attributes for {stack.Collectible.Code}");
                    return;
                }
                else if (materialData.StartsWith("BASICATTR:"))
                {
                    string attrsStr = materialData.Substring(10);
                    var attrParts = attrsStr.Split('|');
                    
                    foreach (var attrPart in attrParts)
                    {
                        var parts = attrPart.Split(':');
                        if (parts.Length >= 3)
                        {
                            string key = parts[0];
                            string type = parts[1];
                            string value = parts[2];
                            
                            try
                            {
                                switch (type)
                                {
                                    case "string":
                                        stack.Attributes.SetString(key, value);
                                        break;
                                    case "int":
                                        stack.Attributes.SetInt(key, int.Parse(value));
                                        break;
                                    case "float":
                                        stack.Attributes.SetFloat(key, float.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
                                        break;
                                    case "bool":
                                        stack.Attributes.SetBool(key, bool.Parse(value));
                                        break;
                                }
                            }
                            catch (Exception ex)
                            {
                                sapi.Logger.Warning($"Error processing attribute {key} of type {type}: {ex.Message}");
                            }
                        }
                    }
                    
                    sapi.Logger.Debug($"Successfully processed basic attributes for {stack.Collectible.Code}");
                    return;
                }
                else if (!string.IsNullOrEmpty(materialData))
                {
                    var attributes = materialData.Split('|')
                        .Select(m => m.Split(':'))
                        .Where(parts => parts.Length == 2)
                        .ToDictionary(parts => parts[0], parts => parts[1]);

                    foreach (var attr in attributes)
                    {
                        try
                        {
                            switch (attr.Key)
                            {
                                case "wood":
                                case "metal":
                                case "color":
                                case "deco":
                                case "construction":
                                case "material":
                                    stack.Attributes.SetString(attr.Key, attr.Value);
                                    break;
                                case "pieSize":
                                    stack.Attributes.SetInt("pieSize", int.Parse(attr.Value));
                                    break;
                                case "topCrustType":
                                    stack.Attributes.SetInt("topCrustType", int.Parse(attr.Value));
                                    break;
                                case "bakeLevel":
                                    stack.Attributes.SetFloat("bakeLevel", float.Parse(attr.Value));
                                    break;
                                case "bakeable":
                                    stack.Attributes.SetBool("bakeable", bool.Parse(attr.Value));
                                    break;
                                case "liquid":
                                    stack.Attributes.SetString("liquid", attr.Value);
                                    break;
                                case "fillLevel":
                                    stack.Attributes.SetFloat("fillLevel", float.Parse(attr.Value));
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            sapi.Logger.Warning($"Error processing attribute {attr.Key}: {ex.Message}");
                        }
                    }
                    
                    sapi.Logger.Debug($"Successfully processed legacy attributes for {stack.Collectible.Code}");
                }
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"Error processing attributes for {stack.Collectible.Code}: {ex.Message}");
            }
        }

        private string FormatTimeRemaining(TimeSpan remaining)
        {
            if (remaining.TotalDays >= 1)
            {
                int days = (int)remaining.TotalDays;
                int hours = remaining.Hours;
                int minutes = remaining.Minutes;
                
                if (hours == 0 && minutes == 0)
                    return $"{days} дн.";
                    
                if (minutes == 0)
                    return $"{days} дн. {hours} ч.";
                    
                return $"{days} дн. {hours} ч. {minutes} мин.";
            }
            
            if (remaining.TotalHours >= 1)
            {
                int hours = (int)remaining.TotalHours;
                int minutes = remaining.Minutes;
                
                if (minutes == 0)
                    return $"{hours} ч.";
                    
                return $"{hours} ч. {minutes} мин.";
            }
            
            if (remaining.TotalMinutes >= 1)
            {
                int minutes = (int)remaining.TotalMinutes;
                int seconds = remaining.Seconds;
                
                if (seconds == 0)
                    return $"{minutes} мин.";
                    
                return $"{minutes} мин. {seconds} сек.";
            }
            
            return $"{remaining.TotalSeconds:F0} сек.";
        }

        private void LoadData()
        {
            try
            {
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    kits = JsonConvert.DeserializeObject<KitData>(json);
                }
                kits ??= new KitData();
            }
            catch (Exception e)
            {
                sapi.Server.LogError($"Failed loading kit data: {e}");
                kits = new KitData();
            }
        }

        private void SaveData()
        {
            try
            {
                var settings = new JsonSerializerSettings 
                {
                    ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                    Formatting = Formatting.Indented
                };
                
                string json = JsonConvert.SerializeObject(kits, settings);
                File.WriteAllText(configPath, json);
            }
            catch (Exception e)
            {
                sapi.Server.LogError($"Failed saving kit data: {e}");
            }
        }

        public ItemStack GetItemFromInventory(IInventory inventory, string itemCode)
        {
            var itemStack = inventory.FirstOrDefault(item => item.Itemstack?.Collectible.Code.ToString() == itemCode);
            if (itemStack != null)
            {
                return new ItemStack(itemStack.Itemstack.Id, EnumItemClass.Item, itemStack.Itemstack.StackSize, itemStack.Itemstack.Attributes as TreeAttribute, sapi.World);
            }
            return null;
        }

        private void InitializeItemPrototypes()
        {
            try
            {
                sapi.Logger.Debug("Initializing item prototypes for kit system...");
                
                foreach (var item in sapi.World.Items)
                {
                    if (item.Code == null) continue;
                    
                    try
                    {
                        var prototype = new ItemStack(item);
                        itemPrototypes[item.Code] = prototype;
                    }
                    catch (Exception ex)
                    {
                        sapi.Logger.Warning($"Failed to create prototype for item {item.Code}: {ex.Message}");
                    }
                }
                
                foreach (var block in sapi.World.Blocks)
                {
                    if (block.Code == null) continue;
                    
                    try
                    {
                        var prototype = new ItemStack(block);
                        itemPrototypes[block.Code] = prototype;
                    }
                    catch (Exception ex)
                    {
                        sapi.Logger.Warning($"Failed to create prototype for block {block.Code}: {ex.Message}");
                    }
                }
                
                sapi.Logger.Debug($"Initialized {itemPrototypes.Count} item prototypes");
            }
            catch (Exception ex)
            {
                sapi.Logger.Error($"Error initializing item prototypes: {ex.Message}");
            }
        }

        private List<KitItem> LoadInventoryItemsForKit(IInventory inventory)
        {
            var items = new List<KitItem>();

            for (int i = 0; i < inventory.Count; i++)
            {
                var slot = inventory[i];
                if (slot.Empty) continue;

                string itemCode = slot.Itemstack.Collectible.Code.ToString();
                
                string materialData = ExtractItemAttributes(slot.Itemstack);
                
                var kitItem = new KitItem
                {
                    Code = itemCode,
                    StackSize = slot.Itemstack.StackSize,
                    Material = materialData
                };

                items.Add(kitItem);
                
                sapi.Logger.Debug($"Added item to kit: {itemCode} x{slot.Itemstack.StackSize}, Material: {(materialData == null ? "none" : "data present")}");
                
                if (slot.Itemstack.Attributes != null && slot.Itemstack.Attributes.Count > 0)
                {
                    LogItemAttributes(slot.Itemstack, $"Attributes for {itemCode} added to kit");
                }
            }

            return items;
        }
    }

    public class KitData
    {
        public Dictionary<string, Kit> Kits = new Dictionary<string, Kit>();
        public Dictionary<string, PlayerKits> PlayerData = new Dictionary<string, PlayerKits>();
    }

    public class Kit
    {
        public string Type;
        public List<KitItem> Items = new List<KitItem>();
        public int CooldownMinutes;
        public List<string> RequiredPrivileges = new List<string>();
    }

    public class KitItem
    {
        public string Code;
        public int StackSize;
        public string Material;
    }

    public class PlayerKits
    {
        public HashSet<string> ClaimedSingleKits = new HashSet<string>();
        public Dictionary<string, KitCooldown> MultiKitCooldowns = new Dictionary<string, KitCooldown>();
    }

    public class KitCooldown
    {
        public long LastClaimTime;
        public long NextAvailableTime;
    }

    public class KitSystem
    {
        private IWorldAccessor world;
        public Dictionary<string, KitData> KitContents { get; set; }
        
        public KitSystem(IWorldAccessor world)
        {
            this.world = world;
        }
        
        public void SaveKit(string kitName, List<ItemStack> items)
        {
            KitContents = new Dictionary<string, KitData>();
            
            foreach (var item in items)
            {
                var kitData = new KitData();
                var kit = new Kit();
                var kitItem = new KitItem 
                {
                    Code = item.Collectible.Code.ToString(),
                    StackSize = item.StackSize,
                    Material = item.Collectible.Variant["material"] as string
                };
                kit.Items.Add(kitItem);
                kitData.Kits[kitName] = kit;
                KitContents[kitName] = kitData;
            }
        }
        
        public List<ItemStack> LoadKit(string kitName)
        {
            var items = new List<ItemStack>();
            
            foreach (var content in KitContents)
            {
                var item = world.GetItem(new AssetLocation(content.Key));
                if (item != null)
                {
                    var stack = new ItemStack(item, content.Value.Kits[kitName].Items[0].StackSize);
                    if (!string.IsNullOrEmpty(content.Value.Kits[kitName].Items[0].Material))
                    {
                        stack.Collectible.Variant["material"] = content.Value.Kits[kitName].Items[0].Material;
                    }
                    items.Add(stack);
                }
            }
            
            return items;
        }
    }
}



