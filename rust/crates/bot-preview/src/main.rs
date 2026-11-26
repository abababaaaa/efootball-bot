#![cfg_attr(windows, windows_subsystem = "windows")]

//! gpui 预览窗骨架：开一个窗口显示帧环状态。
//! S4 把占位色块换成 vision-svc 帧环的 DX11 纹理（零拷贝）。

use gpui::prelude::*;
use gpui::{
    div, rgb, App, Bounds, Context, IntoElement, Render, TitlebarOptions, Window, WindowOptions,
};

struct Preview;

impl Render for Preview {
    fn render(&mut self, _window: &mut Window, _cx: &mut Context<Self>) -> impl IntoElement {
        div()
            .size_full()
            .bg(rgb(0x08_08_0a))
            .flex()
            .flex_col()
            .items_center()
            .justify_center()
            .child(div().text_xl().text_color(rgb(0xff_ff_ff)).child("EfootballBot 预览窗"))
            .child(
                div()
                    .mt_2()
                    .text_sm()
                    .text_color(rgb(0x98_98_9f))
                    .child("gpui 骨架 · 帧环接入待 S4"),
            )
    }
}

fn main() {
    gpui_platform::application().run(|cx: &mut App| {
        cx.open_window(
            WindowOptions {
                titlebar: Some(TitlebarOptions {
                    title: Some("EfootballBot · 预览 (gpui)".into()),
                    ..Default::default()
                }),
                window_bounds: Some(gpui::WindowBounds::Windowed(Bounds {
                    origin: gpui::point(gpui::px(100.0), gpui::px(100.0)),
                    size: gpui::size(gpui::px(480.0), gpui::px(270.0)),
                })),
                ..Default::default()
            },
            |_, cx| cx.new(|_| Preview),
        )
        .expect("打开预览窗失败");
    });
}
