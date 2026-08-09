# Encontrar páginas

Hay tres formas de encontrar algo, y cada una sirve para algo distinto.

## El cuadro de búsqueda: buscar dentro de las páginas

El cuadro de arriba busca en el **texto completo** de todas las páginas que puedes leer: títulos,
encabezados, cuerpo, etiquetas y rutas. No es una simple búsqueda de nombres de página.

Escribe y pulsa Intro. Los resultados muestran el título, dónde está la página, un fragmento con tus
palabras resaltadas, sus etiquetas y cuándo se actualizó por última vez.

### Sintaxis de búsqueda

Escribir palabras sueltas funciona. Cuando necesites precisión:

| Lo que escribes | Qué hace |
|---|---|
| `vpn cisco` | Encuentra páginas que contengan **ambas** palabras |
| `"sitio a sitio"` | Busca esa **frase exacta** |
| `-obsoleto` | **Excluye** las páginas que contengan esa palabra |
| `tag:seguridad` | Solo páginas con esa etiqueta |
| `in:IT/VPN` | Solo páginas dentro de esa carpeta |
| `owner:ana` | Solo páginas de esa responsable |
| `lang:es` | Solo páginas en ese idioma |
| `updated:>2026-01-01` | Solo páginas modificadas después de esa fecha |

Se combinan entre sí: `firewall in:IT -borrador` busca páginas sobre firewall dentro de *IT* que no
mencionen *borrador*.

### Cosas que hace por ti

- **Los acentos dan igual.** Buscar `sesion` encuentra *sesión*, y `politica` encuentra *política*.
- **La última palabra se completa sola.** Escribir `servidor` también encuentra *servidores*, así
  que aparecen resultados mientras sigues escribiendo.
- **Tu idioma va primero.** Cuando una página existe en dos idiomas, la que coincide con el idioma
  de tu interfaz aparece más arriba, pero la otra sigue estando.

### Si no sale nada

Prueba con menos palabras: la búsqueda exige *todas*, así que una consulta larga es una consulta
estrecha. Revisa la ortografía, y recuerda que una página que no puedes leer nunca aparecerá,
tampoco en el recuento de resultados.

## Ctrl-K: ir a una página que ya conoces

Pulsa **Ctrl-K** (**Cmd-K** en un Mac) y empieza a escribir el nombre de una página. Es la vía más
rápida cuando sabes cómo se llama y solo quieres abrirla. Tolera nombres parciales y con alguna
errata.

## El árbol y las etiquetas: explorar

El panel de la izquierda es el árbol de carpetas, que suele reflejar cómo está organizada tu
organización.

La pantalla de **Etiquetas** lista todas las que se usan con su recuento, y al pulsar una ves sus
páginas. Los recuentos se calculan para ti en concreto, así que una etiqueta nunca revela páginas
que no puedas abrir.

**Actualizado recientemente** es una buena forma de ver qué se ha movido últimamente.

## Sobre el índice

La búsqueda se apoya en un índice que se reconstruye solo a medida que cambian las páginas. Justo
después de una importación grande o de una reconstrucción puede aparecer un aviso de que los
resultados podrían estar incompletos. Desaparece por sí mismo.
