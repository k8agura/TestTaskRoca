# Справки без очередей

Веб-сервис для заявок на бухгалтерские справки: сотрудник подаёт заявку через браузер, бухгалтерия видит очередь и меняет статусы. Данные хранятся в памяти процесса — после перезапуска всё обнуляется.

## Что нужно установить

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) — больше ничего, фронтенд это обычный `index.html` без сборки.

Проверить, что SDK на месте:

```bash
dotnet --version
```

## Как запустить

Из корня репозитория:

```bash
dotnet run --project Test.ApiService
```

Откройте `http://localhost:5545/` — это и веб-интерфейс, и API.

По HTTPS (профиль `https`):

```bash
dotnet run --project Test.ApiService --launch-profile https
```

Тогда сервис поднимется ещё и на `https://localhost:7373/`.

В Visual Studio просто выберите проект `Test.ApiService` и нажмите запуск.

## Полезные адреса

- `http://localhost:5545/` — веб-интерфейс
- `http://localhost:5545/api` — описание API
- `http://localhost:5545/openapi/v1.json` — OpenAPI-схема (только в Development)

Готовые примеры запросов лежат в `Test.ApiService/Test.ApiService.http`.

## Эндпоинты

| Метод | Путь | Что делает |
|-------|------|------------|
| `GET` | `/api/certificate-types` | список типов справок |
| `POST` | `/api/requests/` | создать заявку |
| `GET` | `/api/requests/{id}` | заявка по идентификатору |
| `GET` | `/api/requests/employee/{employeeNumber}` | заявки одного сотрудника |
| `GET` | `/api/accounting/requests` | очередь бухгалтерии (фильтр `?status=`) |
| `PATCH` | `/api/accounting/requests/{id}/status` | сменить статус заявки |

Тело POST-запроса (ключи на русском — так ждёт бэкенд):

```json
{
  "табельныйНомер": "EMP-001",
  "фиоСотрудника": "Иван Петров",
  "типСправки": "TwoNdfl",
  "количествоЭкземпляров": 2,
  "причина": "для банка",
  "названиеПроизвольнойСправки": null
}
```
## Ссылка на видео
https://drive.google.com/file/d/1hIHtSuSBxbqC7nmdd2KMGJDuBUR-72EK/view?usp=sharing
