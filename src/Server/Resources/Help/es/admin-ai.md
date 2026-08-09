# Configurar el asistente de IA

**Administración → Asistente de IA.** Totalmente opcional. Sin configurar, no aparece ningún control
de IA en el producto: no en gris, sino ausente.

## Conectar un proveedor

Un único endpoint compatible con OpenAI cubre Ollama, Groq, OpenAI, Azure OpenAI, LM Studio y vLLM.

- **URL base**: la base de chat-completions, sin la ruta final. Para Ollama en la misma máquina:
  `http://localhost:11434/v1`
- **Modelo**: un modelo instruct estándar, por ejemplo `llama-3.3-70b-versatile`. Los modelos de
  razonamiento (gpt-oss, DeepSeek-R1, serie o) son aquí más lentos y más caros y no se recomiendan:
  esta carga de trabajo es resumir y reescribir, no resolver acertijos.
- **Clave de API**: opcional. Ollama y LM Studio no la necesitan. Una vez guardada, deja el campo en
  blanco para conservarla o escribe otra para sustituirla.

**Probar conexión** pide al modelo que responda y te muestra lo que contestó. Hazlo antes de anunciar
que la función existe.

## La decisión de privacidad

El panel lo dice sin rodeos: el contenido de las páginas se envía al endpoint que configures cada vez
que alguien usa una acción de IA. **Un modelo que corre en vuestro propio servidor lo mantiene en esa
máquina.** Esa es toda la razón por la que el endpoint es configurable en lugar de fijo.

Las páginas dentro de carpetas cifradas quedan excluidas salvo que actives esa carpeta expresamente.

## Límites de uso

Un endpoint de pago cobra por petición, así que acota lo que el wiki puede gastar en 24 horas
móviles:

- **Peticiones por persona y día**: sé generoso. Un editor que repasa una docena de páginas gasta
  unas cuarenta. Si aprietas demasiado, la gente deja de fiarse de la función.
- **Peticiones para todos por día**: un segundo techo para toda la instancia. Déjalo en 0 (sin
  límite) salvo que el endpoint sea de pago por uso.

Una petición que falle en el proveedor también cuenta, porque a esas alturas ya ha costado dinero.

El panel muestra el uso de las últimas 24 horas y quién más ha pedido: sirve para detectar una
integración desbocada, no para evaluar a nadie.

## Dónde se puede usar la IA

- **Espacios permitidos**: carpetas de primer nivel que el asistente puede leer. Vacío significa
  todas. Úsalo para dejar un área entera fuera del alcance del asistente sin necesidad de cifrarla.
- **Funciones**: desactiva funciones concretas: mejorar la redacción, redactar desde notas, resumir,
  traducir, preguntar al wiki, pistas de actualidad. Una función sin marcar **desaparece del
  producto** en lugar de fallar cuando alguien la usa.

## Desactivarlo

**Desactivar la IA** retira todos los controles de IA de todas partes, al momento. No se envía nada a
ninguna parte. La configuración se conserva, así que volver a activarlo no implica reintroducir el
endpoint.
