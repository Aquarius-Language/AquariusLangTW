#pragma once
#include <GLFW/glfw3.h>

/* The bridge owns one of these states per GLFW window, independent of rendering. */
int aqua_text_create(GLFWwindow* window, GLFWcharfun character);
void aqua_text_destroy(GLFWwindow* window);
void aqua_text_character(GLFWwindow* window, unsigned int codepoint);
