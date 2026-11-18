# PaddleOCR #10534 - pad-to-batch width noise (collaborator ToddBear)
https://github.com/PaddlePaddle/PaddleOCR/issues/10534
ToddBear (Collaborator):
"det和rec之间的关联是这样的：det完成后，会将所有检测出的文字区域的图片作为一个batch输入到识别模型中
该batch内的所有图片会被resize到同一高度，对于'宽高比'较低的图片，需要在右侧补0，以保证所有图片的宽度是一样的
随后该batch的图片就会被送入识别模块中生成识别结果
我猜测导致上述现象的原因是补0操作引入了噪声信息，使得宽高比较低的文本（即只有几个字的文本）容易被识别错误。"
=> The zero padding on the right of a short (low aspect-ratio) crop is real input to the
   recogniser. This is exactly the mechanism by which a tiny/isolated crop can decode into a
   spurious character. Confirmed in RapidOCR source: resize_norm_img() zero-pads to
   padding_im[:, :, 0:resized_w], rest stays 0, and width = img_height * max_wh_ratio,
   where max_wh_ratio is the MAX over the whole batch (RapidOCR ch_ppocr_rec/main.py).
