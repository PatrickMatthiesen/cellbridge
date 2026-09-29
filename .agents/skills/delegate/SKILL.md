---
name: delegate
description: Copilot only. This project is too large for you to do the work alone. Use runSubagent to split up the work. This tells you how to use the other models when you dont have the names.
---

Copilot has a bug so you dont get the names of the other models.
If more models exist you can find it in 
"C:\Users\<user>\AppData\Roaming\Code\User\chatLanguageModels.json" windows
"/mnt/c/Users/<user>/AppData/Roaming/Code/User/chatLanguageModels.json"

The format when you need to request a model is "<name> (<provider>)" using the model parameter and not the agentName.
Use it with runSubagent to get it to review, investigate or solve issues you are not able to do yourself.
Consider using it if you have been going for a while and maybe need a clean context window agent to find the issue with a less narrow view on the issue.
