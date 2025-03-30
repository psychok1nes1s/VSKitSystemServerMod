# KitSystem - Система наборов предметов / Item Kit System

## Русская версия

### Описание

KitSystem - это мод для Vintage Story, добавляющий возможность создавать и выдавать наборы предметов (киты) игрокам. Администраторы могут создавать различные наборы из предметов в своём инвентаре и настраивать условия их получения.

### Особенности

- **Два типа китов**: одноразовые (выдаются один раз) и многоразовые (с перезарядкой)
- **Сохранение атрибутов предметов**: материалы, цвета, качество и другие свойства
- **Требование привилегий**: возможность ограничить доступ к китам по привилегиям(Не работает в данный момент)
- **Система перезарядки**: настраиваемое время ожидания для повторного получения кита
- **Интуитивное управление**: простые команды для создания и получения китов
- **Корректная обработка особых предметов**: еда (всегда свежая), ведра с жидкостями и другие сложные предметы выдаются правильно

### Команды

#### Для администраторов

- **`/kit create <название> [время_перезарядки]`**  
  Создаёт новый кит из предметов в панели быстрого доступа.  
  Пример: `/kit create starter 1440` - создаст кит "starter" с перезарядкой 24 часа.

- **`/kit delete <название>`**  
  Удаляет существующий кит.  
  Пример: `/kit delete starter`

#### Для игроков

- **`/kit list`**  
  Показывает список всех доступных китов.

- **`/kit claim <название>`**  
  Получает указанный кит.  
  Пример: `/kit claim starter`

- **`/kit info <название>`**  
  Показывает подробную информацию о ките.  
  Пример: `/kit info starter`

- **`/kit showplayerkits`**  
  Показывает статус всех китов для текущего игрока (полученные одноразовые киты, киты на перезарядке и доступные киты).

### Типы китов

- **Одноразовые киты**: могут быть получены игроком только один раз.
- **Многоразовые киты**: могут быть получены несколько раз, но с периодом ожидания между получениями.

### Привилегии

> **ВАЖНО:** В текущей версии функционал привилегий работает некорректно. **Рекомендуется не указывать привилегии при создании китов**. В будущем будет разработан отдельный мод для контроля прав с API, который будет интегрирован с этой системой китов.

- Для получения всех китов необходима базовая привилегия `chat`.
- Для создания и удаления китов требуется привилегия `controlserver`.
- Для некоторых китов могут требоваться дополнительные привилегии, указанные при создании(Не работает на данный момент).

### Примеры использования

1. **Создание стартового набора:**  
   `/kit create starter 0`  
   Создаст одноразовый кит "starter", доступный всем игрокам.

2. **Создание ежедневного ресурсного пакета:**  
   `/kit create daily 1440`  
   Создаст многоразовый кит "daily" с перезарядкой 24 часа (1440 минут), доступный всем игрокам.

3. **Создание еженедельного набора:**  
   `/kit create premium 10080`  
   Создаст многоразовый кит "premium" с перезарядкой 7 дней (10080 минут).

### Особенности работы с предметами

- **Все предметы еды** создаются свежими на момент выдачи.
- **Ведра с жидкостями** сохраняют тип жидкости и уровень заполнения.
- **Предметы с атрибутами** (материалы, цвета и т.д.) сохраняют все свои свойства.
- **Улучшенные инструменты и оружие** сохраняют материалы и другие характеристики.

### Примечания

- При использовании команды `/kit create` предметы должны находиться в панели быстрого доступа.
- Удаление кита также удаляет всю информацию о его получении у игроков.
- Администраторы не имеют ограничений на получение китов.

---

## English version

### Description

KitSystem is a Vintage Story mod that adds the ability to create and distribute item kits to players. Administrators can create various kits from items in their inventory and configure conditions for obtaining them.

### Features

- **Two types of kits**: single-use (issued once) and multi-use (with cooldown)
- **Item attribute preservation**: materials, colors, quality, and other properties
- **Privilege requirements**: ability to restrict access to kits by privileges(Not working in current version)
- **Cooldown system**: customizable waiting time for receiving a kit again
- **Intuitive management**: simple commands for creating and obtaining kits
- **Proper handling of special items**: food (always fresh), buckets with liquids, and other complex items

### Commands

#### For Administrators

- **`/kit create <name> [cooldown_minutes]`**  
  Creates a new kit from items in the hotbar.  
  Example: `/kit create starter 1440` - creates a "starter" kit with a 24-hour cooldown.

- **`/kit delete <name>`**  
  Deletes an existing kit.  
  Example: `/kit delete starter`

#### For Players

- **`/kit list`**  
  Shows a list of all available kits.

- **`/kit claim <name>`**  
  Claims the specified kit.  
  Example: `/kit claim starter`

- **`/kit info <name>`**  
  Shows detailed information about a kit.  
  Example: `/kit info starter`

- **`/kit showplayerkits`**  
  Shows the status of all kits for the current player (claimed single-use kits, kits on cooldown, and available kits).

### Kit Types

- **Single-use kits**: can be claimed by a player only once.
- **Multi-use kits**: can be claimed multiple times, but with a waiting period between claims.

### Privileges

> **IMPORTANT:** In the current version, the privilege functionality is not working correctly. **It is recommended not to specify privileges when creating kits**. In the future, a separate mod will be developed for permission control with an API that will be integrated with this kit system.

- The basic `chat` privilege is required to claim any kit.
- Creating and deleting kits requires the `controlserver` privilege.
- Some kits may require additional privileges specified during creation.

### Usage Examples

1. **Creating a starter kit:**  
   `/kit create starter 0`  
   Creates a single-use "starter" kit available to all players.

2. **Creating a daily resource package:**  
   `/kit create daily 1440`  
   Creates a multi-use "daily" kit with a 24-hour cooldown (1440 minutes), available to all players.

3. **Creating a weekly package:**  
   `/kit create premium 10080`  
   Creates a multi-use "premium" kit with a 7-day cooldown (10080 minutes).

### Special Item Handling

- **All food items** are created fresh at the time of issuance.
- **Buckets with liquids** preserve the liquid type and fill level.
- **Items with attributes** (materials, colors, etc.) retain all their properties.
- **Enhanced tools and weapons** preserve materials and other characteristics.

### Notes

- When using the `/kit create` command, items must be in the hotbar.
- Deleting a kit also removes all information about its receipt by players.
- Administrators can bypass all restrictions on receiving kits. 