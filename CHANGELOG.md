# Cambios

## 2.3.7

- Nuevo icono de Windows con cofre dorado simplificado y fondo opaco para que se distinga en la barra de tareas.

## 2.3.6

- El icono del cofre se distingue en los tamaños pequeños de la barra de tareas, la bandeja y los accesos directos de Windows.

## 2.3.5

- **COFRES** y **COFRES (SQUAD)** recuperan su tamaño anterior. El bloque de texto queda más cerca de la cifra en el HUD de Windows y OBS.

## 2.3.4

- El HUD usa un tamaño intermedio para **COFRES** y **COFRES (SQUAD)**. El rótulo queda entre la cifra principal y las etiquetas de detalle, también en OBS.

## 2.3.3

- El filtro «Mostrar partidas desde» excluye los totales manuales sin fecha del contador, las estadísticas y el informe. Al quitarlo se recupera el total histórico.
- El perfil seleccionado sigue activo aunque no tenga partidas en el período.

## 2.3.2

- Enlace al repositorio de GitHub en la ventana, Ajustes y el informe.
- Incluye la actualización del overlay 2.3.1 con **COFRES** y **COFRES (SQUAD)**, rótulos en mayúsculas y Anton como alternativa integrada a Burbank instalada.

## 2.3.0

- Selector de perfil dibujado para la interfaz grafito/dorado: nombre, partidas y menú oscuro con teclado.
- Integración de LED-N 1.5.0: catálogo validado, colección, probabilidades, horas del tesoro, premios vistos y avisos de vehículos.
- Imágenes de colección incluidas en el ejecutable. Marcas manuales por perfil en el almacenamiento del navegador.
- Detección por proximidad de cofres destruidos al romper su soporte, movimiento/asientos de vehículos y corrección del callback de cierre de actores.
- Filtro opcional de BR normal de la temporada para partidas nuevas. Todas las partidas guardadas siguen visibles.
- Minimizar a bandeja opcional, recuperar ventana desde una segunda apertura, confirmación de cierre y ayuda para desinstalar conservando datos.
- Estimación opcional desde Carrera: descuenta partidas de otro PC y mantiene la estimación separada de las aperturas verificadas.
- OBS respeta el estilo HUD/clásico seleccionado.

Se mantienen la interfaz nativa propia y el cofre. No se adopta la ventana WebView2 del proyecto oficial: la interfaz personalizada ya cubre ventana, ajustes, informe y overlay sin añadir ese runtime. La desinstalación se documenta en Ayuda; esta edición portable no añade un instalador que pueda borrar el historial.

No se fuerza el reanálisis de partidas anteriores. La referencia local pasó de 77 a 79 cofres al recuperar dos destrucciones; el historial instalado conserva sus cifras. La atribución por proximidad y los avisos de vehículo tienen las limitaciones de lo grabado en el replay. Una partida nueva completa en Fortnite sigue siendo la validación pendiente del ciclo real.

Verificación: pruebas sintéticas y preservación de una copia real; ventana, selector y ajustes abiertos; ocho pestañas del informe a 1360 y 390 px, imágenes incluidas y persistencia de marcas. Las capturas públicas solo muestran datos ficticios.

## 2.2.0

Interfaz nativa propia inspirada en las pautas de unslop, cofre con transparencia, HUD y estilo clásico.
