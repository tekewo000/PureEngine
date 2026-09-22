#version 450
layout(location=0) in vec2 position;
layout(location=1) in vec2 uv;
layout(location=2) in vec4 color;
layout(location=3) in vec4 clip;
layout(push_constant) uniform Screen { vec2 size; } screen;
layout(location=0) out vec2 texCoord;
layout(location=1) out vec4 tint;
layout(location=2) out vec4 bounds;
layout(location=3) out vec2 logicalPosition;
void main() {
    gl_Position=vec4(position.x/screen.size.x*2.0-1.0,1.0-position.y/screen.size.y*2.0,0,1);
    texCoord=uv; tint=color; bounds=clip; logicalPosition=position;
}
